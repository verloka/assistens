# Application icon

`agent.png` is the final abstract arrow mark generated with the built-in image_gen
tool on 2026-10-18. The user explicitly rejected the interim robot asset; that
asset is not included. No downloaded Fluent Emoji artwork is shipped.

`agent.ico` contains PNG frames at 16/20/24/32/48/64/128/256px, repacked by
`scripts/build-icon.ps1` for the executable, WPF windows and system tray.
The generated PNG is retained with its alpha channel; no runtime download or
external image API credentials are required.

Final generation prompt:

> Use case: logo-brand. Asset type: final Windows desktop app and system tray icon for a professional personal AI assistant. Generate ONE refined minimal abstract icon: a deep charcoal rounded square containing a bold clean ivory geometric upward/rightward chevron or folded arrow, with a single small electric teal accent. Flat vector-like polished geometry, excellent optical balance, recognizable silhouette, crisp high contrast that survives 16px. Centered single square mark, generous padding, transparent outside the rounded-square silhouette. No robot, no head, no eyes, no face, no mascot, no emoji, no brain, no circuit board, no text, no letters, no mockup, no glow, no 3D, no gradients, no shadow, no decorative details, no watermark. Original premium desktop utility visual identity, calm and professional.
