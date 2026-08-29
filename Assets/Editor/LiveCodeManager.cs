#if UNITY_EDITOR
using System;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEngine.Networking;

[Serializable]
public class CodeResponse
{
    public string code;
}

[Serializable]
public class StateResponse
{
    public string state;
}

[InitializeOnLoad]
public class LiveCodeManager
{
    private static readonly string codeServerUrl = "http://localhost:3000/get-code-update";
    private static readonly string stateServerUrl = "http://localhost:3000/get-state-command";
    private static readonly string targetFilePath = "Assets/Scripts/DynamicGameplayLogic.cs";

    private static double lastCheckTime = 0;
    private const double CheckIntervalSeconds = 0.5; // Poll every 0.5s for fast response

    static LiveCodeManager()
    {
        // Register polling tick with the Unity Editor update loop
        EditorApplication.update += PollForServerUpdates;
    }

    private static void PollForServerUpdates()
    {
        // Rate-limit network polling to prevent spamming localhost
        if (EditorApplication.timeSinceStartup - lastCheckTime < CheckIntervalSeconds)
            return;

        lastCheckTime = EditorApplication.timeSinceStartup;

        FetchCodeFromServer();
        FetchStateFromServer();
    }

    private static void FetchCodeFromServer()
    {
        UnityWebRequest webRequest = UnityWebRequest.Get(codeServerUrl);
        var asyncOp = webRequest.SendWebRequest();

        // Non-blocking asynchronous callback
        asyncOp.completed += (op) =>
        {
            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                string jsonResponse = webRequest.downloadHandler.text;
                CodeResponse response = JsonUtility.FromJson<CodeResponse>(jsonResponse);

                if (response != null && !string.IsNullOrEmpty(response.code))
                {
                    ApplyCodeUpdate(response.code);
                }
            }
            webRequest.Dispose();
        };
    }

    private static void FetchStateFromServer()
    {
        UnityWebRequest webRequest = UnityWebRequest.Get(stateServerUrl);
        var asyncOp = webRequest.SendWebRequest();

        // Non-blocking asynchronous callback for Play/Stop commands
        asyncOp.completed += (op) =>
        {
            if (webRequest.result == UnityWebRequest.Result.Success)
            {
                string jsonResponse = webRequest.downloadHandler.text;
                StateResponse response = JsonUtility.FromJson<StateResponse>(jsonResponse);

                if (response != null && !string.IsNullOrEmpty(response.state))
                {
                    if (response.state == "play" && !EditorApplication.isPlaying)
                    {
                        Debug.Log("[LiveCodeManager] Entering Play Mode via Discord command...");
                        EditorApplication.isPlaying = true;
                    }
                    else if (response.state == "stop" && EditorApplication.isPlaying)
                    {
                        Debug.Log("[LiveCodeManager] Exiting Play Mode via Discord command...");
                        EditorApplication.isPlaying = false;
                    }
                }
            }
            webRequest.Dispose();
        };
    }

    private static void ApplyCodeUpdate(string newCode)
    {
        try
        {
            // Ensure directory exists
            string directory = Path.GetDirectoryName(targetFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Write C# file directly to disk
            File.WriteAllText(targetFilePath, newCode);
            Debug.Log($"[LiveCodeManager] Overwrote {targetFilePath}. Triggering AssetDatabase refresh...");

            // Trigger Unity's internal compiler
            AssetDatabase.Refresh();
        }
        catch (Exception ex)
        {
            Debug.LogError($"[LiveCodeManager] Failed to write script: {ex.Message}");
        }
    }
}
#endif