using System;
using System.IO;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.InputSystem; // requires com.unity.inputsystem package
using MoonSharp.Interpreter;

public class MoonSharpOTAManager : MonoBehaviour
{
    [SerializeField] private string serverUrl = "https://YOUR-STATIC-DOMAIN.ngrok-free.dev/get-apk-code-update";
    [SerializeField] private float pollInterval = 3.0f;
    [SerializeField] private Camera targetCamera; // assign in Inspector; falls back to Camera.main
    [SerializeField] private float touchWorldDistanceFromCamera = 10f; // used for perspective cameras

    private Script luaScript;
    private string deviceSavePath;
    private string currentLoadedScript = "";
    private DynValue luaMainFunc = null;
    private DynValue luaUpdateFunc = null;

    private Dictionary<string, GameObject> spawnedObjects = new Dictionary<string, GameObject>();
    private int nextObjectId = 0;
    private string deviceId;

    /// <summary>
    /// Stable per-device identifier used so the server can store and return a
    /// separate script for each device instead of one shared script for everyone.
    /// Cached in PlayerPrefs so it survives app restarts.
    /// </summary>
    public string DeviceId
    {
        get
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                deviceId = PlayerPrefs.GetString("ota_device_id", "");
                if (string.IsNullOrEmpty(deviceId))
                {
                    // SystemInfo.deviceUniqueIdentifier is not reliable/available on all platforms
                    // (e.g. WebGL), so fall back to a generated GUID either way.
                    deviceId = System.Guid.NewGuid().ToString("N");
                    PlayerPrefs.SetString("ota_device_id", deviceId);
                    PlayerPrefs.Save();
                }
            }
            return deviceId;
        }
    }

    private string PollUrlWithDeviceId()
    {
        string separator = serverUrl.Contains("?") ? "&" : "?";
        return $"{serverUrl}{separator}deviceId={UnityWebRequest.EscapeURL(DeviceId)}";
    }

    private void Start()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        deviceSavePath = Path.Combine(Application.persistentDataPath, $"saved_logic_{DeviceId}.lua");

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        CleanBadCache();

        // Load whatever is already saved offline so the game works instantly without waiting for network
        LoadSavedScriptFromDevice();

        // Kick off background polling for live updates
        StartCoroutine(PollForOTAUpdates());
    }

    private void Update()
    {
        if (luaScript != null && luaUpdateFunc != null && luaUpdateFunc.Type == DataType.Function)
        {
            try
            {
                luaScript.Call(luaUpdateFunc, Time.deltaTime);
            }
            catch (ScriptRuntimeException ex)
            {
                Debug.LogError($"❌ [Lua Update Runtime Error]: {ex.DecoratedMessage}");
                luaUpdateFunc = null; // Disable to prevent log spam
            }
            catch (Exception ex)
            {
                Debug.LogError($"❌ [Lua Update Error]: {ex.Message}");
                luaUpdateFunc = null;
            }
        }
    }

    private void CleanBadCache()
    {
        if (File.Exists(deviceSavePath))
        {
            try
            {
                string content = File.ReadAllText(deviceSavePath);
                if (string.IsNullOrEmpty(content) || content.Contains("<!DOCTYPE html>") || content.Contains("<html"))
                {
                    Debug.LogWarning("⚠ [OTA] Detected HTML or corrupted data in local cache. Purging file.");
                    File.Delete(deviceSavePath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Cache Clean Error]: {ex.Message}");
            }
        }
    }

    private Script CreateFreshLuaEngine()
    {
        Script freshScript = new Script();
        Table unityTable = new Table(freshScript);

        // Core API Bindings — keep this list in sync with the SYSTEM_PROMPT in server.js
        unityTable["DebugLog"] = (Action<string>)LuaDebugLog;
        unityTable["SetBackgroundColor"] = (Action<float, float, float>)LuaSetBackgroundColor;
        unityTable["SpawnObject"] = (Func<string, string>)LuaSpawnObject;
        unityTable["SetPosition"] = (Action<string, float, float, float>)LuaSetPosition;
        unityTable["SetScale"] = (Action<string, float, float, float>)LuaSetScale;

        // Mobile Touch Input Bindings
        unityTable["GetTouchX"] = (Func<float>)LuaGetTouchX;
        unityTable["IsTouching"] = (Func<bool>)LuaIsTouching;

        freshScript.Globals["Unity"] = unityTable;
        return freshScript;
    }

    private void LoadSavedScriptFromDevice()
    {
        if (File.Exists(deviceSavePath))
        {
            try
            {
                string savedCode = File.ReadAllText(deviceSavePath);
                if (!string.IsNullOrEmpty(savedCode) && !savedCode.Contains("<!DOCTYPE html>") && !savedCode.Contains("<html"))
                {
                    currentLoadedScript = savedCode;
                    ExecuteLuaScript(savedCode);
                }
                else
                {
                    Debug.LogWarning("⚠ [Local Save] Cached script contains HTML or is invalid. Purging.");
                    File.Delete(deviceSavePath);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Local Save Error]: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Call this right after a successful /generate POST so the game doesn't have
    /// to wait for the next scheduled poll to pick up the new script.
    /// </summary>
    public void RequestImmediateRefresh()
    {
        StartCoroutine(FetchOnce());
    }

    private IEnumerator FetchOnce()
    {
        using (UnityWebRequest www = UnityWebRequest.Get(PollUrlWithDeviceId()))
        {
            www.SetRequestHeader("ngrok-skip-browser-warning", "true");
            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                string incomingLuaCode = www.downloadHandler.text.Trim();
                if (!string.IsNullOrEmpty(incomingLuaCode) &&
                    incomingLuaCode != currentLoadedScript &&
                    !incomingLuaCode.Contains("<!DOCTYPE html>") &&
                    !incomingLuaCode.Contains("<html"))
                {
                    Debug.Log("🔄 [OTA] Immediate refresh fetched new script!");
                    currentLoadedScript = incomingLuaCode;
                    SaveScriptToDevice(incomingLuaCode);
                    ExecuteLuaScript(incomingLuaCode);
                }
            }
            else
            {
                Debug.LogWarning($"⚠ [OTA] Immediate refresh failed: {www.error}");
            }
        }
    }

    private IEnumerator PollForOTAUpdates()
    {
        while (true)
        {
            yield return new WaitForSeconds(pollInterval);

            using (UnityWebRequest www = UnityWebRequest.Get(PollUrlWithDeviceId()))
            {
                www.SetRequestHeader("ngrok-skip-browser-warning", "true");
                yield return www.SendWebRequest();

                if (www.result == UnityWebRequest.Result.Success)
                {
                    string incomingLuaCode = www.downloadHandler.text.Trim();

                    // Ensure response is valid and does not contain ngrok/HTML error screens
                    if (!string.IsNullOrEmpty(incomingLuaCode) &&
                        incomingLuaCode != currentLoadedScript &&
                        !incomingLuaCode.Contains("<!DOCTYPE html>") &&
                        !incomingLuaCode.Contains("<html"))
                    {
                        Debug.Log("🔄 [OTA] New valid Lua script received!");
                        currentLoadedScript = incomingLuaCode;
                        SaveScriptToDevice(incomingLuaCode);
                        ExecuteLuaScript(incomingLuaCode);
                    }
                    else if (incomingLuaCode.Contains("<!DOCTYPE html>") || incomingLuaCode.Contains("<html"))
                    {
                        Debug.LogWarning("⚠ [OTA Warning] Server returned an HTML page instead of Lua code. Ignored.");
                    }
                }
                else
                {
                    Debug.LogWarning($"⚠ [OTA] Poll failed: {www.error}");
                }
            }
        }
    }

    private void SaveScriptToDevice(string code)
    {
        try { File.WriteAllText(deviceSavePath, code); }
        catch (Exception ex) { Debug.LogError($"[Save Error]: {ex.Message}"); }
    }

    private void ExecuteLuaScript(string code)
    {
        try
        {
            ClearSpawnedObjects();
            luaScript = CreateFreshLuaEngine();

            Debug.Log($"🔍 [Lua Payload Inspection]:\n{code}");

            luaScript.DoString(code);

            // Resolve Main() — required entry point
            DynValue mainFunc = luaScript.Globals.Get("Main");
            if (mainFunc != null && mainFunc.Type == DataType.Function)
            {
                luaMainFunc = mainFunc;
                luaScript.Call(luaMainFunc);
            }
            else
            {
                Debug.LogWarning("⚠ [Lua] Script has no global function 'Main()'. Nothing will be spawned.");
                luaMainFunc = null;
            }

            // Resolve Update(deltaTime) — optional but expected
            DynValue updateVal = luaScript.Globals.Get("Update");
            if (updateVal != null && updateVal.Type == DataType.Function)
            {
                luaUpdateFunc = updateVal;
            }
            else
            {
                luaUpdateFunc = null;
            }
        }
        catch (ScriptRuntimeException ex)
        {
            Debug.LogError($"❌ [MoonSharp Runtime Error]: {ex.DecoratedMessage}");
        }
        catch (SyntaxErrorException ex)
        {
            Debug.LogError($"❌ [Execution Error]: {ex.DecoratedMessage}");
        }
        catch (Exception ex)
        {
            Debug.LogError($"❌ [Lua Execution Error]: {ex.Message}");
        }
    }

    private void ClearSpawnedObjects()
    {
        foreach (var kvp in spawnedObjects)
        {
            if (kvp.Value != null)
            {
                Destroy(kvp.Value);
            }
        }
        spawnedObjects.Clear();
        luaMainFunc = null;
        luaUpdateFunc = null;
    }

    // ---------------------------------------------------------------
    // Lua-bound functions (must match server.js SYSTEM_PROMPT exactly)
    // ---------------------------------------------------------------

    private void LuaDebugLog(string message)
    {
        Debug.Log($"[Lua] {message}");
    }

    private void LuaSetBackgroundColor(float r, float g, float b)
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
        if (targetCamera == null)
        {
            Debug.LogWarning("⚠ [Lua] SetBackgroundColor called but no camera found in scene.");
            return;
        }
        targetCamera.clearFlags = CameraClearFlags.SolidColor;
        targetCamera.backgroundColor = new Color(
            Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
    }

    private string LuaSpawnObject(string objectType)
    {
        PrimitiveType primitive;
        switch ((objectType ?? "Cube").Trim().ToLowerInvariant())
        {
            case "sphere": primitive = PrimitiveType.Sphere; break;
            case "cylinder": primitive = PrimitiveType.Cylinder; break;
            case "capsule": primitive = PrimitiveType.Capsule; break;
            case "quad": primitive = PrimitiveType.Quad; break;
            case "plane": primitive = PrimitiveType.Plane; break;
            case "cube":
            default: primitive = PrimitiveType.Cube; break;
        }

        GameObject go = GameObject.CreatePrimitive(primitive);

        // IMPORTANT: CreatePrimitive assigns the legacy built-in "Default-Diffuse" shader.
        // Under URP/HDRP that renders invisible/pink. Explicitly assign a pipeline-safe material.
        Renderer rend = go.GetComponent<Renderer>();
        if (rend != null)
        {
            Shader urpLit = Shader.Find("Universal Render Pipeline/Lit");
            Shader standard = Shader.Find("Standard");
            Shader shaderToUse = urpLit != null ? urpLit : standard;
            if (shaderToUse != null)
            {
                rend.material = new Material(shaderToUse);
            }
        }

        string id = "obj_" + (nextObjectId++);
        go.name = id;
        spawnedObjects[id] = go;
        return id;
    }

    private void LuaSetPosition(string id, float x, float y, float z)
    {
        if (string.IsNullOrEmpty(id) || !spawnedObjects.TryGetValue(id, out GameObject go) || go == null)
        {
            Debug.LogWarning($"⚠ [Lua] SetPosition called with unknown id '{id}'.");
            return;
        }
        go.transform.position = new Vector3(x, y, z);
    }

    private void LuaSetScale(string id, float x, float y, float z)
    {
        if (string.IsNullOrEmpty(id) || !spawnedObjects.TryGetValue(id, out GameObject go) || go == null)
        {
            Debug.LogWarning($"⚠ [Lua] SetScale called with unknown id '{id}'.");
            return;
        }
        go.transform.localScale = new Vector3(x, y, z);
    }

    private float LuaGetTouchX()
    {
        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }
        if (targetCamera == null) return 0f;

        Vector2 screenPos = GetPointerScreenPosition();

        Vector3 worldPos;
        if (targetCamera.orthographic)
        {
            worldPos = targetCamera.ScreenToWorldPoint(
                new Vector3(screenPos.x, screenPos.y, targetCamera.nearClipPlane));
        }
        else
        {
            worldPos = targetCamera.ScreenToWorldPoint(
                new Vector3(screenPos.x, screenPos.y, touchWorldDistanceFromCamera));
        }

        return worldPos.x;
    }

    private bool LuaIsTouching()
    {
        // Real touchscreen (device)
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            return true;
        }
        // Mouse fallback (Editor / desktop testing)
        if (Mouse.current != null && Mouse.current.leftButton.isPressed)
        {
            return true;
        }
        return false;
    }

    private Vector2 GetPointerScreenPosition()
    {
        if (Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed)
        {
            return Touchscreen.current.primaryTouch.position.ReadValue();
        }
        if (Mouse.current != null)
        {
            return Mouse.current.position.ReadValue();
        }
        return Vector2.zero;
    }
}