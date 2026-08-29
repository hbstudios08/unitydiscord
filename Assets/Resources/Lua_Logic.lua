ballName = "Ball"

function Start()
    UI.Log("Spawning Ball...")
    -- Spawn at runtime: name, x, y, radius
    UI.SpawnBall(ballName, 0, 2, 0.5)
end

function OnTouch()
    UI.Log("Touch: Bounce")
    UI.SetGravity(ballName, 0) -- no gravity
    UI.AddForce(ballName, 0, 15) -- bounce
end

function OnRelease()
    UI.Log("Release: Gravity On")
    UI.SetGravity(ballName, 1) -- gravity back
end