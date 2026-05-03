---
name: dns-add-record
description: 'Add a new DNS record via the DnsGoBetween API.'
---

# Add DNS Record

Use this skill to create a new DNS record (A, AAAA, CNAME, or PTR).

1. Prepare a JSON payload with `ZoneName`, `HostName`, `RecordType`, and `Data`.
   - `RecordType` must be one of: `A`, `AAAA`, `CNAME`, `PTR`.
   - `TimeToLive` is optional (defaults to 3600).
2. Call the `POST /api/records` endpoint.
3. Example PowerShell usage:
   ```powershell
   $body = @{
       ZoneName = "ashurtech.net"
       HostName = "test-node"
       RecordType = "A"
       Data = "10.0.0.50"
       TimeToLive = 3600
   } | ConvertTo-Json
   
   Invoke-RestMethod -Uri "http://localhost:6790/api/records" -Method Post -Body $body -ContentType "application/json" -UseDefaultCredentials
   ```
4. Note: This requires Admin permissions in the API configuration.
