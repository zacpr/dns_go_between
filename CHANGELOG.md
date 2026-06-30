# Changelog

## Unreleased

- **UI**: Fixed a rendering bug in the DNS tree where records could appear duplicated. Tree now groups records by host name.
- **Diagnostics**: Added `scripts/cleanup-fqdn-duplicates.ps1` (dry-run by default) for auditing zones that may contain records stored with their fully-qualified name.
