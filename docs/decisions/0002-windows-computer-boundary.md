# Windows computer adapter boundary

Date: 2026-10-18. Status: boundary selected; PA-008 attended spike verified, production PA-024–PA-048 pending.

Keep capture and input in `PersonalAgent.Computer`, behind Core contracts.
Use native Windows capture and `SendInput`; do not add browser automation, UIA
or a third-party Computer Use framework. Keep screen/image coordinates explicit
and account for DPI and virtual desktop origin before enabling real input.

PA-008's standalone `tests/PersonalAgent.WindowsSmoke` was run at user request:
the user observed a cropped GDI capture (850x700 physical pixels, DPI 120, one
monitor, virtual origin 0,0), one Unicode `x` from SendInput in its own test field,
and Ctrl+Shift+F10 WM_HOTKEY. Closing returned exit 0. Exact OS/elevation was not
reported. This is limited diagnostic evidence, not a production driver/lease or
multi-monitor/mixed-DPI/lock/UAC acceptance. Limitations: `tests/README.md`. Desktop
input must wait for the lease/idle policy, and user takeover must revoke access
locally, release held inputs and invalidate stale coordinates before resume.

References: Development Plan §§5–6, 16 Phase 0;
[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
