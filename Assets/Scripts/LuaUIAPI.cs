using UnityEngine;
using System.IO;
using MoonSharp.Interpreter;

[MoonSharpUserData]
public class UI_Helpers
{
    public void SetText(string objectName, string text)
    {
        var obj = GameObject.Find(objectName);
        if (obj != null)
        {
            if (obj.TryGetComponent(out TMPro.TextMeshProUGUI tmp)) tmp.text = text;
            else if (obj.TryGetComponent(out UnityEngine.UI.Text oldUI)) oldUI.text = text;
        }
    }

    public void SetActive(string objectName, bool state)
    {
        var obj = GameObject.Find(objectName);
        if (obj != null) obj.SetActive(state);
    }

    public void SetSlider(string objectName, float value)
    {
        var obj = GameObject.Find(objectName);
        if (obj != null && obj.TryGetComponent(out UnityEngine.UI.Slider slider))
            slider.value = value;
    }


    public void SetGravity(string objectName, float scale)
    {
        var obj = GameObject.Find(objectName);
        if (obj != null && obj.TryGetComponent(out Rigidbody2D rb))
            rb.gravityScale = scale;
    }

    public void AddForce(string objectName, float x, float y)
    {
        var obj = GameObject.Find(objectName);
        if (obj != null && obj.TryGetComponent(out Rigidbody2D rb))
            rb.AddForce(new Vector2(x, y));
    }

    // NEW: Spawn objects at runtime
    public void SpawnBall(string objectName, float x, float y, float radius)
    {
        if (GameObject.Find(objectName) != null) return; // don't spawn 2

        GameObject ball = new GameObject(objectName);
        ball.transform.position = new Vector2(x, y);

        // Add Sprite so you can see it
        var sr = ball.AddComponent<SpriteRenderer>();
        sr.sprite = CreateCircleSprite(radius);
        sr.color = Color.red;

        // Add Physics
        var col = ball.AddComponent<CircleCollider2D>();
        col.radius = radius;
        var rb = ball.AddComponent<Rigidbody2D>();
        rb.gravityScale = 1;

        // Add EventTrigger so Lua can get touch
        var trigger = ball.AddComponent<UnityEngine.EventSystems.EventTrigger>();
        AddEvent(trigger, UnityEngine.EventSystems.EventTriggerType.PointerDown, "OnTouch");
        AddEvent(trigger, UnityEngine.EventSystems.EventTriggerType.PointerUp, "OnRelease");

        Log("Spawned: " + objectName);
    }

    public void DestroyObject(string objectName)
    {
        var obj = GameObject.Find(objectName);
        if (obj != null) GameObject.Destroy(obj);
    }

    void AddEvent(UnityEngine.EventSystems.EventTrigger trigger, UnityEngine.EventSystems.EventTriggerType type, string luaFunc)
    {
        var entry = new UnityEngine.EventSystems.EventTrigger.Entry();
        entry.eventID = type;
        entry.callback.AddListener((data) => { LuaUIAPI.Instance.RunLuaFunction(luaFunc); });
        trigger.triggers.Add(entry);
    }

    Sprite CreateCircleSprite(float radius)
    {
        Texture2D tex = new Texture2D(128, 128);
        //... basic white circle. For prod use a real sprite
        Color[] colors = new Color[128 * 128];
        for (int i = 0; i < colors.Length; i++) colors[i] = Color.white;
        tex.SetPixels(colors); tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, 128, 128), new Vector2(0.5f, 0.5f), 100);
    }

    public void Log(string msg) { Debug.Log("[LUA] " + msg); }
}

public class LuaUIAPI : MonoBehaviour
{
    public static LuaUIAPI Instance; // so UI Buttons can find it
    Script lua;
    string luaPath;

    void Awake()
    {
        Instance = this;
        lua = new Script(CoreModules.Preset_Default);
        UserData.RegisterAssembly();
        lua.Globals["UI"] = new UI_Helpers();

        luaPath = Path.Combine(Application.persistentDataPath, "UI_Logic.lua");
        if (!File.Exists(luaPath)) File.WriteAllText(luaPath, "-- Empty\n");

        string luaCode = File.ReadAllText(luaPath);
        lua.DoString(luaCode); // Load the file

        // SAFE CALL: only run Start() if it exists
        DynValue startFunc = lua.Globals.Get("Start");
        if (startFunc != null && startFunc.Type == DataType.Function)
        {
            lua.Call(startFunc); // Run Start()
            Debug.Log("Called Lua Start()");
        }
        else
        {
            Debug.Log("No Start() function found in Lua. Skipping.");
        }

        Debug.Log("Lua Loaded: " + luaPath);
    }
    // Call this from Button OnClick in Inspector
    public void CallFromButton(string funcName)
    {
        RunLuaFunction(funcName);
    }

    public void RunLuaFunction(string funcName)
    {
        DynValue func = lua.Globals.Get(funcName);
        if (func != null && func.Type == DataType.Function)
        {
            func.Function.Call();
        }
        else
        {
            Debug.LogError($"Function '{funcName}' not found in UI_Logic.lua");
        }
    }

    public void ReloadLua(string newCode)
    {
        File.WriteAllText(luaPath, newCode);
        lua.DoString(newCode);
        Debug.Log("Lua Reloaded!");
    }
}