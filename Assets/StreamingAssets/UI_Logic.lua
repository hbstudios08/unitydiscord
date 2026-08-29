-- Update ONLY this file from Discord/API

function OnStartButtonClick()
    UI.Log("Start button pressed")
    UI.SetActive("MainMenu", false)
    UI.SetActive("GameHUD", true)
    UI.SetText("ScoreText", "Score: 0")
end

function OnSettingsButtonClick()
    UI.Log("Opening settings")
    UI.SetActive("SettingsPanel", true)
end

function UpdateHealthBar(currentHP, maxHP)
    local percent = currentHP / maxHP
    UI.SetSlider("HealthBar", percent)
    UI.SetText("HPText", currentHP.. "/".. maxHP)
end

function ShowGameOver(finalScore)
    UI.SetActive("GameOverPanel", true)
    UI.SetText("FinalScoreText", "Score: ".. finalScore)
end

function Test()
    UI.Log("Lua is working!")
    UI.SetText("ScoreText", "Hello from Lua")
end