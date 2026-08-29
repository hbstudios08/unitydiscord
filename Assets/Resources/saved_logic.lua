local paddle1Id = nil
local paddle2Id = nil
local ballId = nil

local ballX = 0.0
local ballY = 0.0
local ballVX = 3.0
local ballVY = 5.0

local p1X = 0.0
local p2X = 0.0

function Main()
    Unity.SetBackgroundColor(0.1, 0.1, 0.15)
    
    paddle1Id = Unity.SpawnObject("Cube")
    Unity.SetScale(paddle1Id, 2.0, 0.4, 0.5)
    Unity.SetPosition(paddle1Id, 0.0, -4.5, 0.0)
    
    paddle2Id = Unity.SpawnObject("Cube")
    Unity.SetScale(paddle2Id, 2.0, 0.4, 0.5)
    Unity.SetPosition(paddle2Id, 0.0, 4.5, 0.0)
    
    ballId = Unity.SpawnObject("Sphere")
    Unity.SetScale(ballId, 0.6, 0.6, 0.6)
    Unity.SetPosition(ballId, 0.0, 0.0, 0.0)
    
    ballX = 0.0
    ballY = 0.0
    p1X = 0.0
    p2X = 0.0
    
    Unity.DebugLog("Pong initialized. Touch screen to move bottom paddle.")
end

function Update(deltaTime)
    if Unity.IsTouching() then
        local touchX = Unity.GetTouchX()
        p1X = touchX
    end
    
    p2X = ballX * 0.7
    
    if p1X < -4.0 then p1X = -4.0 end
    if p1X > 4.0 then p1X = 4.0 end
    
    if p2X < -4.0 then p2X = -4.0 end
    if p2X > 4.0 then p2X = 4.0 end
    
    ballX = ballX + ballVX * deltaTime
    ballY = ballY + ballVY * deltaTime
    
    if ballX < -4.7 then
        ballX = -4.7
        ballVX = -ballVX
    elseif ballX > 4.7 then
        ballX = 4.7
        ballVX = -ballVX
    end
    
    if ballY <= -4.3 and ballY >= -4.7 then
        if ballX >= (p1X - 1.2) and ballX <= (p1X + 1.2) then
            ballY = -4.3
            ballVY = -ballVY
            ballVX = ballVX + (ballX - p1X) * 2.0
        end
    end
    
    if ballY >= 4.3 and ballY <= 4.7 then
        if ballX >= (p2X - 1.2) and ballX <= (p2X + 1.2) then
            ballY = 4.3
            ballVY = -ballVY
            ballVX = ballVX + (ballX - p2X) * 2.0
        end
    end
    
    if ballY < -6.0 or ballY > 6.0 then
        ballX = 0.0
        ballY = 0.0
        ballVX = 3.0
        ballVY = 5.0
        Unity.DebugLog("Point scored! Resetting ball.")
    end
    
    Unity.SetPosition(paddle1Id, p1X, -4.5, 0.0)
    Unity.SetPosition(paddle2Id, p2X, 4.5, 0.0)
    Unity.SetPosition(ballId, ballX, ballY, 0.0)
end