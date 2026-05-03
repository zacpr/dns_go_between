---
name: dns-delete-record
description: 'Delete an existing DNS record via the DnsGoBetween API.'
---

# Delete DNS Record

Use this skill to remove a DNS record. Deletion requires an exact match of all properties for safety.

1. Prepare a JSON payload with `ZoneName`, `HostName`, `RecordType`, and `Data`.
2. Call the `DELETE /api/records` endpoint.
3. Example PowerShell usage:
   ```powershell
   $body = @{
       ZoneName = "ashurtech.net"
       HostName = "test-node"
       RecordType = "A"
       Data = "10.0.0.50"
   } | ConvertTo-Json
   
   Invoke-RestMethod -Uri "http://localhost:6790/api/records" -Method Delete -Body $body -ContentType "application/json" -UseDefaultCredentials
   ```
4. Verification: After deletion, use `dns-get-records` to confirm the record is gone.
