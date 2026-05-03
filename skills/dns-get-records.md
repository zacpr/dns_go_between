---
name: dns-get-records
description: 'Retrieve DNS records for a specific zone from the DnsGoBetween API.'
---

# Retrieve DNS Records

Use this skill when you need to fetch the current DNS records for a zone (e.g., ashurtech.net).

1. Ensure the DnsGoBetween service is running on the target machine (port 6790).
2. Call the `GET /api/zones/{zone}/records` endpoint.
3. Example PowerShell usage:
   ```powershell
   $zone = "ashurtech.net"
   $url = "http://localhost:6790/api/zones/$($zone)/records"
   Invoke-RestMethod -Uri $url -Method Get -UseDefaultCredentials
   ```
4. The API returns an array of `DnsRecord` objects including `HostName`, `RecordType`, and `Data`.
