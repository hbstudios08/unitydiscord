using UnityEngine;
using UnityEngine.UI;
using TMPro;

/// <summary>
/// In-game prompt UI. Each player supplies their own AI provider API key (entered once,
/// cached in PlayerPrefs on-device). Generation happens entirely client-side via
/// AIRequestClient — no server involved.
/// </summary>
public class InGamePromptUI : MonoBehaviour
{
    [Header("UI References")]
    [SerializeField] private TMP_InputField promptInput;      // swap for TMP_InputField if using TextMeshPro
    [SerializeField] private TMP_InputField aiApiKeyInput;     // player's own key; set Content Type to Password in the Inspector
    [SerializeField] private TMP_Dropdown aiProviderDropdown;  // options must be exactly: Gemini, Groq, OpenRouter (in that order)
    [SerializeField] private Button submitButton;
    [SerializeField] private Button saveKeyButton;         // optional; submit also saves automatically
    [SerializeField] private TextMeshProUGUI statusText;              // swap for TMP_Text if using TextMeshPro

    [Header("Scene References")]
    [SerializeField] private MoonSharpOTAManager otaManager;
    [SerializeField] private AIRequestClient aiClient;

    private const string PrefKeyApiKey = "player_ai_api_key";
    private const string PrefKeyProvider = "player_ai_provider";

    private bool isGenerating = false;

    private void Awake()
    {
        if (submitButton != null) submitButton.onClick.AddListener(OnSubmitPressed);
        if (saveKeyButton != null) saveKeyButton.onClick.AddListener(SaveKeyAndProvider);
    }

    private void Start()
    {
        if (aiApiKeyInput != null)
        {
            aiApiKeyInput.text = PlayerPrefs.GetString(PrefKeyApiKey, "");
        }
        if (aiProviderDropdown != null)
        {
            aiProviderDropdown.value = PlayerPrefs.GetInt(PrefKeyProvider, 0);
        }
    }

    private void SaveKeyAndProvider()
    {
        if (aiApiKeyInput != null)
        {
            PlayerPrefs.SetString(PrefKeyApiKey, aiApiKeyInput.text.Trim());
        }
        if (aiProviderDropdown != null)
        {
            PlayerPrefs.SetInt(PrefKeyProvider, aiProviderDropdown.value);
        }
        PlayerPrefs.Save();
        SetStatus("API key saved on this device.");
    }

    private void OnSubmitPressed()
    {
        if (isGenerating) return;

        string prompt = promptInput != null ? promptInput.text.Trim() : "";
        string aiKey = aiApiKeyInput != null ? aiApiKeyInput.text.Trim() : "";
        string provider = ProviderNameFor(aiProviderDropdown != null ? aiProviderDropdown.value : 0);

        if (string.IsNullOrEmpty(prompt))
        {
            SetStatus("Enter a prompt first.");
            return;
        }
        if (string.IsNullOrEmpty(aiKey))
        {
            SetStatus("Enter your AI provider API key first (saved locally, never shared).");
            return;
        }
        if (aiClient == null)
        {
            SetStatus("AIRequestClient is not assigned in the Inspector.");
            return;
        }

        SaveKeyAndProvider();

        isGenerating = true;
        SetStatus("Generating...");
        if (submitButton != null) submitButton.interactable = false;

        string existingScript = otaManager != null ? otaManager.GetCurrentScript() : "";

        aiClient.Generate(provider, aiKey, prompt, existingScript, OnGenerateComplete);
    }

    private void OnGenerateComplete(bool success, string luaOrError, string providerLabel)
    {
        if (submitButton != null) submitButton.interactable = true;
        isGenerating = false;

        if (success)
        {
            SetStatus($"Generated via {providerLabel}. Loading...");
            if (otaManager != null)
            {
                otaManager.LoadAndRunGeneratedScript(luaOrError);
            }
        }
        else
        {
            SetStatus($"Error ({providerLabel}): {luaOrError}");
            Debug.LogError($"❌ [In-Game Prompt] {luaOrError}");
        }
    }

    private string ProviderNameFor(int dropdownIndex)
    {
        switch (dropdownIndex)
        {
            case 0: return "gemini";
            case 1: return "groq";
            case 2: return "openrouter";
            default: return "gemini";
        }
    }

    private void SetStatus(string message)
    {
        if (statusText != null) statusText.text = message;
        Debug.Log($"[InGamePromptUI] {message}");
    }
}