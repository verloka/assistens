# Windows computer adapter boundary

Date: 2026-10-18. Status: planned decision, pending PA-008/PA-024–PA-048 evidence.

Keep capture and input in `PersonalAgent.Computer`, behind Core contracts.
Use native Windows capture and `SendInput`; do not add browser automation, UIA
or a third-party Computer Use framework. Keep screen/image coordinates explicit
and account for DPI and virtual desktop origin before enabling real input.

No capture/input/hotkey implementation or hardware acceptance is claimed here.
PA-008 must record actual session/elevation, DPI and monitor limitations. Desktop
input must wait for the lease/idle policy, and user takeover must revoke access
locally, release held inputs and invalidate stale coordinates before resume.

References: Development Plan §§5–6, 16 Phase 0;
[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput).
