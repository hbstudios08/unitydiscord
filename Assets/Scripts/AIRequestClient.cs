using System;
using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Calls an AI provider's REST API directly from the game, using the player's own API key.
/// No server involved — the key is used for this one request and never persisted here
/// (InGamePromptUI handles local caching of the key via PlayerPrefs).
/// </summary>
public class AIRequestClient : MonoBehaviour
{
    [Header("Groq settings")]
    [Tooltip("Fallback model if the dynamic model list can't be fetched.")]
    [SerializeField] private string groqFallbackModel = "llama-3.3-70b-versatile";

    [Header("OpenRouter settings")]
    [SerializeField] private string openRouterModel = "google/gemini-3.5-flash-lite";

    [Header("Gemini settings")]
    [SerializeField] private string geminiModel = "gemini-3.5-flash-lite";

    // ---------------------------------------------------------------------
    // SYSTEM PROMPT — must exactly match the sandboxed Unity.* API in
    // MoonSharpOTAManager.cs. Also instructs incremental edits instead of
    // full replacement so runtime changes don't discard existing behavior.
    // ---------------------------------------------------------------------
    private const string SYSTEM_PROMPT = @"
You are generating MoonSharp-compatible Lua for a SANDBOXED Unity mobile game.

You do NOT have access to the Unity API, GameObject, Vector3, UnityEngine, PrimitiveType,
GameManager, or any C# type directly. Referencing any of those will crash the script.

The ONLY functions available to you live on the global ""Unity"" table:

- Unity.DebugLog(message)
- Unity.SetBackgroundColor(r, g, b)           -- r,g,b: 0.0-1.0 floats
- Unity.SpawnObject(objectType)               -- ""Cube""|""Sphere""|""Cylinder""|""Quad""|""Capsule""|""Plane"", returns a string id
- Unity.SetPosition(id, x, y, z)
- Unity.SetScale(id, x, y, z)
- Unity.SetColor(id, r, g, b)                 -- r,g,b: 0.0-1.0 floats, changes an object's color
- Unity.GetTouchX()                           -- returns world X (number)
- Unity.IsTouching()                          -- returns true/false

NEVER call anything not in this list. There is no GameObject.CreatePrimitive, no Vector3.new,
no UnityEngine.Object.Destroy, no GameManager, no FindGameObjectsWithTag. None of that exists here.

Your script MUST define exactly these two global functions:

  function Main()
      -- Runs ONCE when the script loads. Spawn everything here.
  end

  function Update(deltaTime)
      -- Runs every frame.
  end

If an EXISTING SCRIPT is provided below, it is what's currently running on this device.
Treat the new request as an INCREMENTAL change to it: preserve all unrelated variables,
functions, and behavior. Modify or add only what's needed. Still output the FULL resulting
script (not a diff), not just the changed part.

Rules:
1. Output ONLY a ```lua code block. No prose outside the block.
2. Use ONLY the Unity.* functions listed above.
3. Store every id returned by Unity.SpawnObject in a local variable/table so Update() can move it.
4. Close every function/if/for/while with 'end'. Never truncate output.
5. No external requires, no coroutines, no os/io libraries.
";

    // ---- JSON DTOs ----
    [Serializable] private class GeminiPart { public string text; }
    [Serializable] private class GeminiContent { public GeminiPart[] parts; }
    [Serializable] private class GeminiGenerationConfig { public int maxOutputTokens; public float temperature; }
    [Serializable] private class GeminiRequest { public GeminiContent[] contents; public GeminiGenerationConfig generationConfig; }
    [Serializable] private class GeminiCandidate { public GeminiContent content; }
    [Serializable] private class GeminiResponse { public GeminiCandidate[] candidates; }

    [Serializable] private class ChatMessage { public string role; public string content; }
    [Serializable] private class ChatRequest { public string model; public ChatMessage[] messages; public float temperature; public int max_tokens; }
    [Serializable] private class ChatChoiceMessage { public string role; public string content; }
    [Serializable] private class ChatChoice { public ChatChoiceMessage message; public string finish_reason; }
    [Serializable] private class ChatResponse { public ChatChoice[] choices; public string model; }

    [Serializable] private class GroqModel { public string id; }
    [Serializable] private class GroqModelsResponse { public GroqModel[] data; }

    /// <summary>
    /// Generates (or incrementally edits) a Lua script. Calls onComplete(success, luaOrError, providerLabel).
    /// provider: "gemini" | "groq" | "openrouter"
    /// </summary>
    public void Generate(string provider, string apiKey, string userPrompt, string existingScript, Action<bool, string, string> onComplete)
    {
        StartCoroutine(GenerateRoutine(provider, apiKey, userPrompt, existingScript, onComplete));
    }

    private IEnumerator GenerateRoutine(string provider, string apiKey, string userPrompt, string existingScript, Action<bool, string, string> onComplete)
    {
        string userMessage = BuildUserMessage(userPrompt, existingScript);
        string rawOutput = null;
        string providerLabel = null;
        string errorMessage = null;

        switch ((provider ?? "").ToLowerInvariant())
        {
            case "gemini":
                yield return CallGemini(apiKey, userMessage, (ok, text, err) => { rawOutput = text; errorMessage = err; });
                providerLabel = $"Google Gemini ({geminiModel})";
                break;
            case "groq":
                yield return CallGroq(apiKey, userMessage, (ok, text, err, modelUsed) => { rawOutput = text; errorMessage = err; providerLabel = $"Groq ({modelUsed})"; });
                break;
            case "openrouter":
                yield return CallOpenRouter(apiKey, userMessage, (ok, text, err) => { rawOutput = text; errorMessage = err; });
                providerLabel = $"OpenRouter ({openRouterModel})";
                break;
            default:
                onComplete(false, $"Unknown provider \"{provider}\".", null);
                yield break;
        }

        if (errorMessage != null)
        {
            onComplete(false, errorMessage, providerLabel);
            yield break;
        }

        string cleanLua = SanitizeAndValidateLua(rawOutput, out string validationError);
        if (validationError != null)
        {
            onComplete(false, validationError, providerLabel);
            yield break;
        }

        onComplete(true, cleanLua, providerLabel);
    }

    private string BuildUserMessage(string userPrompt, string existingScript)
    {
        if (!string.IsNullOrEmpty(existingScript))
        {
            return $"EXISTING SCRIPT:\n```lua\n{existingScript}\n```\n\nNEW REQUEST: \"{userPrompt}\"\n\nCRITICAL: Output the ENTIRE updated script, not just the changed part. Do not truncate.";
        }
        return $"NEW REQUEST: \"{userPrompt}\"\n\nCRITICAL: Output the ENTIRE script from start to finish without truncation.";
    }

    // ---- Gemini ----
    private IEnumerator CallGemini(string apiKey, string userMessage, Action<bool, string, string> callback)
    {
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{geminiModel}:generateContent";

        var reqBody = new GeminiRequest
        {
            contents = new[] { new GeminiContent { parts = new[] { new GeminiPart { text = SYSTEM_PROMPT + "\n\n" + userMessage } } } },
            generationConfig = new GeminiGenerationConfig { maxOutputTokens = 65536, temperature = 0.1f }
        };
        string json = JsonUtility.ToJson(reqBody);

        using (UnityWebRequest www = new UnityWebRequest(url, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("x-goog-api-key", apiKey);

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                callback(false, null, $"Gemini request failed: {www.error} — {www.downloadHandler?.text}");
                yield break;
            }

            try
            {
                var parsed = JsonUtility.FromJson<GeminiResponse>(www.downloadHandler.text);
                string text = parsed?.candidates?[0]?.content?.parts?[0]?.text;
                if (string.IsNullOrEmpty(text))
                {
                    callback(false, null, "Gemini returned an empty response.");
                    yield break;
                }
                callback(true, text, null);
            }
            catch (Exception ex)
            {
                callback(false, null, $"Failed to parse Gemini response: {ex.Message}");
            }
        }
    }

    // ---- Groq (dynamic model list, OpenAI-compatible chat completions) ----
    private IEnumerator CallGroq(string apiKey, string userMessage, Action<bool, string, string, string> callback)
    {
        string[] modelsToTry = { groqFallbackModel };

        using (UnityWebRequest modelsReq = UnityWebRequest.Get("https://api.groq.com/openai/v1/models"))
        {
            modelsReq.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            yield return modelsReq.SendWebRequest();

            if (modelsReq.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    var modelsResp = JsonUtility.FromJson<GroqModelsResponse>(modelsReq.downloadHandler.text);
                    if (modelsResp?.data != null && modelsResp.data.Length > 0)
                    {
                        var filtered = new System.Collections.Generic.List<string>();
                        foreach (var m in modelsResp.data)
                        {
                            if (m.id.Contains("whisper") || m.id.Contains("guard") || m.id.Contains("vision")) continue;
                            filtered.Add(m.id);
                        }
                        if (filtered.Count > 0) modelsToTry = filtered.ToArray();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠ [Groq] Could not parse model list, using fallback model. {ex.Message}");
                }
            }
        }

        foreach (string model in modelsToTry)
        {
            var chatReq = new ChatRequest
            {
                model = model,
                messages = new[]
                {
                    new ChatMessage { role = "system", content = SYSTEM_PROMPT },
                    new ChatMessage { role = "user", content = userMessage }
                },
                temperature = 0.1f,
                max_tokens = 8192
            };
            string json = JsonUtility.ToJson(chatReq);

            using (UnityWebRequest www = new UnityWebRequest("https://api.groq.com/openai/v1/chat/completions", "POST"))
            {
                www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
                www.downloadHandler = new DownloadHandlerBuffer();
                www.SetRequestHeader("Content-Type", "application/json");
                www.SetRequestHeader("Authorization", $"Bearer {apiKey}");

                yield return www.SendWebRequest();

                if (www.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning($"⚠ [Groq] model {model} failed: {www.error}");
                    continue;
                }

                try
                {
                    var parsed = JsonUtility.FromJson<ChatResponse>(www.downloadHandler.text);
                    if (parsed?.choices == null || parsed.choices.Length == 0)
                    {
                        continue;
                    }
                    if (parsed.choices[0].finish_reason == "length")
                    {
                        Debug.LogWarning($"⚠ [Groq] model {model} hit token limit, trying next.");
                        continue;
                    }
                    string text = parsed.choices[0].message?.content;
                    if (!string.IsNullOrEmpty(text))
                    {
                        callback(true, text, null, model);
                        yield break;
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"⚠ [Groq] Failed to parse response for {model}: {ex.Message}");
                }
            }
        }

        callback(false, null, "All Groq models failed or were unavailable for this key.", "none");
    }

    // ---- OpenRouter ----
    private IEnumerator CallOpenRouter(string apiKey, string userMessage, Action<bool, string, string> callback)
    {
        var chatReq = new ChatRequest
        {
            model = openRouterModel,
            messages = new[]
            {
                new ChatMessage { role = "system", content = SYSTEM_PROMPT },
                new ChatMessage { role = "user", content = userMessage }
            },
            temperature = 0.1f,
            max_tokens = 8192
        };
        string json = JsonUtility.ToJson(chatReq);

        using (UnityWebRequest www = new UnityWebRequest("https://openrouter.ai/api/v1/chat/completions", "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("Authorization", $"Bearer {apiKey}");

            yield return www.SendWebRequest();

            if (www.result != UnityWebRequest.Result.Success)
            {
                callback(false, null, $"OpenRouter request failed: {www.error} — {www.downloadHandler?.text}");
                yield break;
            }

            try
            {
                var parsed = JsonUtility.FromJson<ChatResponse>(www.downloadHandler.text);
                string text = parsed?.choices?[0]?.message?.content;
                if (string.IsNullOrEmpty(text))
                {
                    callback(false, null, "OpenRouter returned an empty response.");
                    yield break;
                }
                callback(true, text, null);
            }
            catch (Exception ex)
            {
                callback(false, null, $"Failed to parse OpenRouter response: {ex.Message}");
            }
        }
    }

    // ---- Shared validation (mirrors what the old server.js enforced) ----
    private string SanitizeAndValidateLua(string rawOutput, out string error)
    {
        error = null;
        string cleanLua = (rawOutput ?? "").Trim();

        if (cleanLua.StartsWith("```"))
        {
            int firstNewline = cleanLua.IndexOf('\n');
            if (firstNewline != -1) cleanLua = cleanLua.Substring(firstNewline + 1);
            if (cleanLua.EndsWith("```")) cleanLua = cleanLua.Substring(0, cleanLua.Length - 3);
            cleanLua = cleanLua.Trim();
        }

        if (!cleanLua.Contains("end") || cleanLua.Split('\n').Length < 5)
        {
            error = "Generated script was truncated or malformed. Please retry.";
            return null;
        }

        if (Regex.IsMatch(cleanLua, @"\b(GameObject\.|UnityEngine\.|Vector3\.new|PrimitiveType\.|GameManager\.|FindGameObjectsWithTag)\b"))
        {
            error = "Generated script referenced an API that doesn't exist in the sandbox. Please retry.";
            return null;
        }

        if (!Regex.IsMatch(cleanLua, @"function\s+Main\s*\(\s*\)"))
        {
            error = "Generated script is missing a 'function Main()' entry point. Please retry.";
            return null;
        }
        if (!Regex.IsMatch(cleanLua, @"function\s+Update\s*\(\s*deltaTime\s*\)"))
        {
            error = "Generated script is missing a 'function Update(deltaTime)' entry point. Please retry.";
            return null;
        }

        return cleanLua;
    }
}