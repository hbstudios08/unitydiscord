using System.IO;
using TMPro;
using UnityEngine;

public class AILuaGenerator : MonoBehaviour
{
    public GeminiAPI gemini;
    public LuaUIAPI luaManager;
    public TMP_InputField promptInput;

    public async void OnGenerateButton()
    {
        string key = PlayerPrefs.GetString("GeminiKey"); // player pasted this in settings
        string currentLua = File.ReadAllText(Application.persistentDataPath + "/UI_Logic.lua");

        string aiPrompt = $"You are a Unity MoonSharp expert. Current Lua:\n{currentLua}\n\nTask: {promptInput.text}\nReturn ONLY full Lua code.";

        string newLua = await gemini.GenerateText(key, aiPrompt);

        File.WriteAllText(Application.persistentDataPath + "/UI_Logic.lua", newLua);
        luaManager.RunLuaFunction("Test"); // reload
        Debug.Log("Lua updated by Gemini!");
    }


}