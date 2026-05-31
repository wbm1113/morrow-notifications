$ErrorActionPreference = "Stop"
$base = "http://localhost:5252"
$pass = 0; $fail = 0
# Use a unique tenant name so re-runs never collide with leftover DB data
$tenantName = "TestCorp-$(Get-Date -Format 'HHmmss')"

function Check($label, $actual, $expected) {
    if ($actual -eq $expected) {
        Write-Host "  PASS  $label" -ForegroundColor Green
        $script:pass++
    } else {
        Write-Host "  FAIL  $label  (expected=$expected  actual=$actual)" -ForegroundColor Red
        $script:fail++
    }
}

function Req($method, $path, $body = $null) {
    $uri = "$base$path"
    $params = @{ Method = $method; Uri = $uri; UseBasicParsing = $true }
    if ($body) { $params.Body = ($body | ConvertTo-Json -Compress); $params.ContentType = "application/json" }
    try {
        $resp = Invoke-WebRequest @params
        return @{ status = [int]$resp.StatusCode; body = $resp.Content | ConvertFrom-Json }
    } catch {
        $status = [int]$_.Exception.Response.StatusCode
        $raw = $null
        try { $raw = $_ | Select-Object -ExpandProperty ErrorDetails | Select-Object -ExpandProperty Message | ConvertFrom-Json } catch {}
        return @{ status = $status; body = $raw }
    }
}

Write-Host "`n------------------------------------------------------------" -ForegroundColor Yellow
Write-Host "  END-TO-END TEST RUN  (tenant=$tenantName)" -ForegroundColor Yellow
Write-Host "------------------------------------------------------------`n" -ForegroundColor Yellow

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "-- TENANT CRUD ----------------------------------------------" -ForegroundColor Cyan

# 1. GET all tenants (may already have data from prior runs; that's fine)
$r = Req GET "/api/tenants"
Check "GET /api/tenants returns 200" $r.status 200
$initialCount = $r.body.Count
Write-Host "     existing tenant count: $initialCount"

# 2. POST create tenant
$r = Req POST "/api/tenants" @{ name = $tenantName; rateLimitPerMinute = 100 }
Check "POST /api/tenants -> 201" $r.status 201
$tenantId = $r.body.id
if (-not $tenantId) { Write-Host "FATAL: tenant creation failed. Aborting." -ForegroundColor Red; exit 1 }
Check "  tenant.name = $tenantName" $r.body.name $tenantName
Check "  tenant.isActive = true" $r.body.isActive $true
Check "  tenant.rateLimitPerMinute = 100" $r.body.rateLimitPerMinute 100
Write-Host "     tenantId: $tenantId"

# 3. GET tenant by ID
$r = Req GET "/api/tenants/$tenantId"
Check "GET /api/tenants/{id} -> 200" $r.status 200
Check "  returns correct id" $r.body.id $tenantId

# 4. POST duplicate name -> 409
$r = Req POST "/api/tenants" @{ name = $tenantName; rateLimitPerMinute = 50 }
Check "POST duplicate tenant name -> 409" $r.status 409

# 5. POST missing name -> 400
$r = Req POST "/api/tenants" @{ rateLimitPerMinute = 10 }
Check "POST tenant missing name -> 400" $r.status 400

# 6. POST bad rateLimitPerMinute -> 400
$r = Req POST "/api/tenants" @{ name = "BadRate"; rateLimitPerMinute = 0 }
Check "POST tenant rateLimitPerMinute=0 -> 400" $r.status 400

# 7. PATCH update name + rate
$updatedName = "$tenantName-Updated"
$r = Req PATCH "/api/tenants/$tenantId" @{ name = $updatedName; rateLimitPerMinute = 150 }
Check "PATCH /api/tenants/{id} -> 200" $r.status 200
Check "  name updated" $r.body.name $updatedName
Check "  rateLimitPerMinute updated" $r.body.rateLimitPerMinute 150

# 8. Confirm GET reflects update
$r = Req GET "/api/tenants/$tenantId"
Check "GET after PATCH reflects new name" $r.body.name $updatedName

# 9. PATCH unknown tenant -> 404
$bogusId = [guid]::NewGuid()
$r = Req PATCH "/api/tenants/$bogusId" @{ name = "Ghost" }
Check "PATCH unknown tenant -> 404" $r.status 404

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n-- ROUTING RULES CRUD ---------------------------------------" -ForegroundColor Cyan

# 10. GET rules for tenant (empty)
$r = Req GET "/api/tenants/$tenantId/rules"
Check "GET /rules for tenant -> 200" $r.status 200

# 11. POST rule: order.created -> slack
$r = Req POST "/api/tenants/$tenantId/rules" @{ eventType = "order.created"; channelType = "slack" }
Check "POST rule order.created->slack -> 201" $r.status 201
$ruleSlackId = $r.body.id
Check "  rule.eventType = order.created" $r.body.eventType "order.created"
Check "  rule.channelType = slack" $r.body.channelType "slack"
Check "  rule.isActive = true" $r.body.isActive $true
Write-Host "     ruleSlackId: $ruleSlackId"

# 12. POST rule: order.created -> teams (fan-out)
$r = Req POST "/api/tenants/$tenantId/rules" @{ eventType = "order.created"; channelType = "teams" }
Check "POST rule order.created->teams fan-out -> 201" $r.status 201
$ruleTeamsId = $r.body.id
Write-Host "     ruleTeamsId: $ruleTeamsId"

# 13. POST rule: payment.failed -> slack
$r = Req POST "/api/tenants/$tenantId/rules" @{ eventType = "payment.failed"; channelType = "slack" }
Check "POST rule payment.failed->slack -> 201" $r.status 201
$rulePaymentId = $r.body.id

# 14. GET all rules - should now be 3
$r = Req GET "/api/tenants/$tenantId/rules"
Check "GET /rules returns 3 rules" (@($r.body) | Measure-Object).Count 3

# 15. GET specific rule
$r = Req GET "/api/tenants/$tenantId/rules/$ruleSlackId"
Check "GET /rules/{ruleId} -> 200" $r.status 200
Check "  returns correct rule id" $r.body.id $ruleSlackId

# 16. POST invalid channel type -> 400
$r = Req POST "/api/tenants/$tenantId/rules" @{ eventType = "foo.bar"; channelType = "carrier-pigeon" }
Check "POST rule invalid channelType -> 400" $r.status 400

# 17. POST rule for unknown tenant -> 404
$r = Req POST "/api/tenants/$bogusId/rules" @{ eventType = "x"; channelType = "slack" }
Check "POST rule unknown tenant -> 404" $r.status 404

# 18. PATCH rule - change event type
$r = Req PATCH "/api/tenants/$tenantId/rules/$rulePaymentId" @{ eventType = "payment.refunded" }
Check "PATCH rule eventType -> 200" $r.status 200
Check "  eventType updated" $r.body.eventType "payment.refunded"

# 19. GET the patched rule
$r = Req GET "/api/tenants/$tenantId/rules/$rulePaymentId"
Check "GET after PATCH rule reflects new eventType" $r.body.eventType "payment.refunded"

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n-- EVENT INGESTION ------------------------------------------" -ForegroundColor Cyan

# 20. POST valid event - routed (order.created -> slack + teams)
$r = Req POST "/api/events" @{ tenantId = $tenantId; eventType = "order.created"; payload = '{"orderId":"ord-001","amount":49.99}' }
Check "POST event order.created -> 202 Accepted" $r.status 202
$msgId = $r.body.messageId
Write-Host "     messageId: $msgId"

# 21. POST event - no matching rule -> dead-lettered (202 first, DLQ after processing)
$r = Req POST "/api/events" @{ tenantId = $tenantId; eventType = "unrouted.event"; payload = '{"note":"no rule"}' }
Check "POST event unrouted -> 202 Accepted" $r.status 202

# 22. POST event - unknown tenant -> 404
$r = Req POST "/api/events" @{ tenantId = [guid]::NewGuid().ToString(); eventType = "order.created"; payload = '{}' }
Check "POST event unknown tenant -> 404" $r.status 404

# 23. POST event - missing eventType -> 400
$r = Req POST "/api/events" @{ tenantId = $tenantId; payload = '{}' }
Check "POST event missing eventType -> 400" $r.status 400

# 24. POST event - missing payload -> 400
$r = Req POST "/api/events" @{ tenantId = $tenantId; eventType = "order.created" }
Check "POST event missing payload -> 400" $r.status 400

# 25. POST event - empty tenantId -> 400
$r = Req POST "/api/events" @{ tenantId = "00000000-0000-0000-0000-000000000000"; eventType = "order.created"; payload = '{}' }
Check "POST event empty tenantId -> 400" $r.status 400

# Wait for routing and delivery workers (two-stage pipeline).
Write-Host "     (waiting 3s for background processors...)"
Start-Sleep -Seconds 3

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n-- DEAD LETTERS ---------------------------------------------" -ForegroundColor Cyan

# 26. GET all dead letters
$r = Req GET "/api/dead-letters"
Check "GET /api/dead-letters -> 200" $r.status 200
$dlCount = @($r.body).Count
Write-Host "     total dead letters: $dlCount"
Check "  at least 1 dead letter exists (unrouted.event)" ($dlCount -ge 1) $true

# 27. GET dead letters by tenant
$r = Req GET "/api/dead-letters/tenant/$tenantId"
Check "GET /api/dead-letters/tenant/{id} -> 200" $r.status 200
$tenantDlCount = @($r.body).Count
Write-Host "     tenant dead letters: $tenantDlCount"
Check "  tenant has 1+ dead letter" ($tenantDlCount -ge 1) $true

# Verify the dead letter is for unrouted.event
$dl = @($r.body) | Where-Object { $_.eventType -eq "unrouted.event" } | Select-Object -First 1
Check "  dead letter eventType = unrouted.event" ($dl -ne $null) $true
if ($dl) { Write-Host "     DL failureReason: $($dl.failureReason)" }

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n-- DEACTIVATE / REACTIVATE TENANT ---------------------------" -ForegroundColor Cyan

# 28. Deactivate tenant
$r = Req PATCH "/api/tenants/$tenantId" @{ isActive = $false }
Check "PATCH deactivate tenant -> 200" $r.status 200
Check "  isActive = false" $r.body.isActive $false

# 29. Event for deactivated tenant -> 404 (rate limiter removes inactive tenants; treated as not found)
$r = Req POST "/api/events" @{ tenantId = $tenantId; eventType = "order.created"; payload = '{"x":1}' }
Check "POST event for inactive tenant -> 404" $r.status 404

# 30. Reactivate tenant
$r = Req PATCH "/api/tenants/$tenantId" @{ isActive = $true }
Check "PATCH reactivate tenant -> 200" $r.status 200
Check "  isActive = true" $r.body.isActive $true

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n-- DELETE RULE & TENANT -------------------------------------" -ForegroundColor Cyan

# 31. DELETE one rule
$r = Req DELETE "/api/tenants/$tenantId/rules/$rulePaymentId"
Check "DELETE /rules/{ruleId} -> 204" $r.status 204

# 32. GET deleted rule -> 404
$r = Req GET "/api/tenants/$tenantId/rules/$rulePaymentId"
Check "GET deleted rule -> 404" $r.status 404

# 33. GET rules - now 2 remain
$r = Req GET "/api/tenants/$tenantId/rules"
Check "GET /rules after delete -> 2 rules" (@($r.body) | Measure-Object).Count 2

# 34. DELETE tenant
$r = Req DELETE "/api/tenants/$tenantId"
Check "DELETE /api/tenants/{id} -> 204" $r.status 204

# 35. GET deleted tenant -> 404
$r = Req GET "/api/tenants/$tenantId"
Check "GET deleted tenant -> 404" $r.status 404

# ─────────────────────────────────────────────────────────────────────────────
Write-Host "`n------------------------------------------------------------" -ForegroundColor Yellow
Write-Host "  RESULTS:  $pass passed   $fail failed" -ForegroundColor $(if ($fail -eq 0) { "Green" } else { "Red" })
Write-Host "------------------------------------------------------------`n" -ForegroundColor Yellow

if ($fail -gt 0) { exit 1 }