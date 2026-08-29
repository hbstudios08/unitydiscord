using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

[Serializable]
public class GeminiPart { public string text; }
[Serializable]
public class GeminiContent { public GeminiPart[] parts; }
[Serializable]
public class GeminiRequest { public GeminiContent[] contents; }
[Serializable]
public class GeminiResponse
{
    public Candidate[] candidates;
    [Serializable] public class Candidate { public GeminiContent content; }
}

public class GeminiAPI : MonoBehaviour
{
    private string baseUrl = "https://generativelanguage.googleapis.com/v1beta/models/gemini-1.5-flash:generateContent";

    // Call this from your UI
    public async Task<string> GenerateText(string apiKey, string prompt)
    {
        string url = $"{baseUrl}?key={apiKey}";

        GeminiRequest requestBody = new GeminiRequest
        {
            contents = new GeminiContent[]
            {
                new GeminiContent
                {
                    parts = new GeminiPart[] { new GeminiPart { text = prompt } }
                }
            }
        };

        string json = JsonUtility.ToJson(requestBody);

        using UnityWebRequest req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(System.Text.Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");

        await req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError("Gemini Error: " + req.error + " " + req.downloadHandler.text);
            return null;
        }

        GeminiResponse response = JsonUtility.FromJson<GeminiResponse>(req.downloadHandler.text);
        return response.candidates[0].content.parts[0].text;
    }
}