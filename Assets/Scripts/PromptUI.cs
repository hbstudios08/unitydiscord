using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// Attach to a UI object alongside an InputField (prompt text), a Button (submit),
/// and optionally a Text/TMP element for status. Wire generateEndpoint to your
/// ngrok domain + "/generate", and set apiKey to match API_SHARED_SECRET in server.js.
/// </summary>
public class InGamePromptUI : MonoBehaviour
{
    [Header("Server")]
    [SerializeField] private string generateEndpoint = "https://YOUR-STATIC-DOMAIN.ngrok-free.dev/generate";
    [SerializeField] private string apiKey = ""; // must match API_SHARED_SECRET in .env

    [Header("UI References")]
    [SerializeField] private TMP_InputField promptInput; // swap for TMP_InputField if using TextMeshPro
    [SerializeField] private Button submitButton;
    [SerializeField] private TextMeshProUGUI statusText;         // swap for TMP_Text if using TextMeshPro

    [Header("Scene Reference")]
    [SerializeField] private MoonSharpOTAManager otaManager;

    private bool isGenerating = false;

    private void Awake()
    {
        if (submitButton != null)
        {
            submitButton.onClick.AddListener(OnSubmitPressed);
        }
    }

    private void OnSubmitPressed()
    {
        if (isGenerating) return;

        string prompt = promptInput != null ? promptInput.text.Trim() : "";
        if (string.IsNullOrEmpty(prompt))
        {
            SetStatus("Enter a prompt first.");
            return;
        }

        StartCoroutine(SubmitPrompt(prompt));
    }

    private IEnumerator SubmitPrompt(string prompt)
    {
        isGenerating = true;
        SetStatus("Generating...");
        if (submitButton != null) submitButton.interactable = false;

        string jsonBody = "{\"prompt\":\"" + EscapeJson(prompt) + "\",\"deviceId\":\"" + EscapeJson(otaManager != null ? otaManager.DeviceId : "") + "\"}";
        byte[] bodyBytes = Encoding.UTF8.GetBytes(jsonBody);

        using (UnityWebRequest www = new UnityWebRequest(generateEndpoint, "POST"))
        {
            www.uploadHandler = new UploadHandlerRaw(bodyBytes);
            www.downloadHandler = new DownloadHandlerBuffer();
            www.SetRequestHeader("Content-Type", "application/json");
            www.SetRequestHeader("ngrok-skip-browser-warning", "true");
            if (!string.IsNullOrEmpty(apiKey))
            {
                www.SetRequestHeader("x-api-key", apiKey);
            }

            yield return www.SendWebRequest();

            if (www.result == UnityWebRequest.Result.Success)
            {
                SetStatus("Generated! Loading...");
                if (otaManager != null)
                {
                    otaManager.RequestImmediateRefresh();
                }
            }
            else
            {
                string errorBody = www.downloadHandler != null ? www.downloadHandler.text : "";
                SetStatus($"Error: {www.error}\n{errorBody}");
                Debug.LogError($"❌ [In-Game Prompt] {www.error} — {errorBody}");
            }
        }

        if (submitButton != null) submitButton.interactable = true;
        isGenerating = false;
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[InGamePromptUI] {message}");
    }

    // Minimal JSON string escaping — sufficient for plain prompt text.
    private string EscapeJson(string s)
    {
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "");
    }
}