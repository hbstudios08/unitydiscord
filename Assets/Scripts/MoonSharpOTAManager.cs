using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine;
using MoonSharp.Interpreter;

/// <summary>
/// Runs Lua code entirely locally on this device — no server, no polling.
/// AIRequestClient generates new scripts by calling an AI provider directly, then
/// hands the result to LoadAndRunGeneratedScript() below.
/// </summary>
public class MoonSharpOTAManager : MonoBehaviour
{
    [SerializeField] private Camera targetCamera; // assign in Inspector; falls back to Camera.main
    [SerializeField] private float touchWorldDistanceFromCamera = 10f; // used for perspective cameras

    [Header("Offline Fallback")]
    [Tooltip("Used ONLY when this device has never cached or generated a script of its own (e.g. very first launch with no network / before any prompt has been submitted).")]
    [SerializeField] private TextAsset bundledFallbackScript;

    private Script luaScript;
    private string deviceSavePath;
    private string currentLoadedScript = "";
    private DynValue luaMainFunc = null;
    private DynValue luaUpdateFunc = null;

    private Dictionary<string, GameObject> spawnedObjects = new Dictionary<string, GameObject>();
    private int nextObjectId = 0;

    private void Start()
    {
        Screen.sleepTimeout = SleepTimeout.NeverSleep;
        deviceSavePath = Path.Combine(Application.persistentDataPath, "saved_logic.lua");

        if (targetCamera == null)
        {
            targetCamera = Camera.main;
        }

        CleanBadCache();

        // Priority: 1) last successful AI-generated script cached on this device,
        // 2) bundled offline fallback library.
        bool loadedFromCache = LoadSavedScriptFromDevice();
        if (!loadedFromCache && bundledFallbackScript != null)
        {
            Debug.Log("📦 [Offline] No cached script found — running bundled fallback script.");
            LoadAndRunGeneratedScript(bundledFallbackScript.text, saveToDisk: false);
        }
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

    /// <summary>
    /// Entry point for AIRequestClient (or the bundled fallback) to push a new script in.
    /// Caches it to disk (so it survives app restarts) and executes it immediately.
    /// </summary>
    public void LoadAndRunGeneratedScript(string luaCode, bool saveToDisk = true)
    {
        if (string.IsNullOrEmpty(luaCode))
        {
            Debug.LogWarning("⚠ [Lua] LoadAndRunGeneratedScript called with empty code. Ignored.");
            return;
        }

        bool unchanged = luaCode == currentLoadedScript;
        Debug.Log($"🔍 [Lua] Loading new script (unchanged from previous: {unchanged}):\n{luaCode}");

        currentLoadedScript = luaCode;
        if (saveToDisk)
        {
            SaveScriptToDevice(luaCode);
        }
        ExecuteLuaScript(luaCode);
    }

    /// <summary>
    /// The script currently running, used as "existing code" context when asking the AI
    /// to make an incremental change instead of starting from scratch.
    /// </summary>
    public string GetCurrentScript()
    {
        return currentLoadedScript;
    }

    private void CleanBadCache()
    {
        if (File.Exists(deviceSavePath))
        {
            try
            {
                string content = File.ReadAllText(deviceSavePath);
                if (string.IsNullOrEmpty(content))
                {
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

        // Core API Bindings — keep this list in sync with the SYSTEM_PROMPT in AIRequestClient.cs
        unityTable["DebugLog"] = (Action<string>)LuaDebugLog;
        unityTable["SetBackgroundColor"] = (Action<float, float, float>)LuaSetBackgroundColor;
        unityTable["SpawnObject"] = (Func<string, string>)LuaSpawnObject;
        unityTable["SetPosition"] = (Action<string, float, float, float>)LuaSetPosition;
        unityTable["SetScale"] = (Action<string, float, float, float>)LuaSetScale;
        unityTable["SetColor"] = (Action<string, float, float, float>)LuaSetColor;

        // Mobile Touch Input Bindings
        unityTable["GetTouchX"] = (Func<float>)LuaGetTouchX;
        unityTable["IsTouching"] = (Func<bool>)LuaIsTouching;

        freshScript.Globals["Unity"] = unityTable;
        return freshScript;
    }

    private bool LoadSavedScriptFromDevice()
    {
        if (File.Exists(deviceSavePath))
        {
            try
            {
                string savedCode = File.ReadAllText(deviceSavePath);
                if (!string.IsNullOrEmpty(savedCode))
                {
                    currentLoadedScript = savedCode;
                    ExecuteLuaScript(savedCode);
                    return true;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Local Save Error]: {ex.Message}");
            }
        }
        return false;
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
    // Lua-bound functions (must match AIRequestClient.cs SYSTEM_PROMPT exactly)
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

    private void LuaSetColor(string id, float r, float g, float b)
    {
        if (string.IsNullOrEmpty(id) || !spawnedObjects.TryGetValue(id, out GameObject go) || go == null)
        {
            Debug.LogWarning($"⚠ [Lua] SetColor called with unknown id '{id}'.");
            return;
        }
        Renderer rend = go.GetComponent<Renderer>();
        if (rend != null)
        {
            rend.material.color = new Color(Mathf.Clamp01(r), Mathf.Clamp01(g), Mathf.Clamp01(b), 1f);
        }
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
        if (UnityEngine.InputSystem.Touchscreen.current != null && UnityEngine.InputSystem.Touchscreen.current.primaryTouch.press.isPressed)
        {
            return true;
        }
        if (UnityEngine.InputSystem.Mouse.current != null && UnityEngine.InputSystem.Mouse.current.leftButton.isPressed)
        {
            return true;
        }
        return false;
    }

    private Vector2 GetPointerScreenPosition()
    {
        if (UnityEngine.InputSystem.Touchscreen.current != null && UnityEngine.InputSystem.Touchscreen.current.primaryTouch.press.isPressed)
        {
            return UnityEngine.InputSystem.Touchscreen.current.primaryTouch.position.ReadValue();
        }
        if (UnityEngine.InputSystem.Mouse.current != null)
        {
            return UnityEngine.InputSystem.Mouse.current.position.ReadValue();
        }
        return Vector2.zero;
    }
}