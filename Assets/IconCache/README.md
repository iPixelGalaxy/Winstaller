# Icon Cache

Winstaller resolves these PNG files from GitHub before Microsoft Store, winget, and website fallbacks.

Use `Scripts/update-icon-cache.ps1 -PackageId <winget-id> -SourcePath <installed-exe-or-png>` to add or replace an icon. Pass multiple package IDs to share one image.

Files must be transparent 256×256 PNGs. `index.json` maps package IDs to files and SHA-256 checksums. Icons remain property of their respective owners and are used only as application identification artwork.