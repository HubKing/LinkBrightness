LinkBrightness
==============

LinkBrightness links the brightnesses of Plugged In and On Battery together, so
that if you change one, the other is also changed:

[![console](screenshots/console.png?raw=true)](screenshots/console.png?raw=true)

If LinkBrightness is running in its own console window (ie not with another
process attached such as command prompt) then it has an icon in the system
tray. You can show/hide the window by double-clicking the tray icon:

[![tray_tooltip](screenshots/tray_tooltip.png?raw=true)](screenshots/tray_tooltip.png?raw=true)

You can exit LinkBrightness by closing the window or right-clicking on the tray
icon and choosing Exit from the popup menu:

[![tray_context_menu](screenshots/tray_context_menu.png?raw=true)](screenshots/tray_context_menu.png?raw=true)

LinkBrightness can run from multiple user accounts at the same time safely. If
one instance is in the middle of syncing the brightness then the others will
not interfere. Since Windows' power-scheme brightness settings are global it
should not matter which user account LinkBrightness is running from. It can run
from administrator and limited user accounts, but cannot run from the Guest
account due to lack of privileges.

Usage
-----

`LinkBrightness [/hide_on_start] [/hide_on_minimize] [/verbose]`

The options pretty much do what they say. The hide options only work if
LinkBrightness is running in its own console window. You can run
`LinkBrightness /?` to see the full details of each option.

Probably most people using this program will want to run it in the background
automatically at login. To do that open your startup folder (Type
`shell:startup` into the Run box (Win+R)) and create a shortcut to
LinkBrightness with the Run properties set to 'Minimized', and use command-line
option `/hide_on_start` (and optionally `/hide_on_minimize` to minimize to
tray).

Or to create a shortcut for all users put it in this folder instead:

`%ProgramData%\Microsoft\Windows\Start Menu\Programs\StartUp`

Why..
-----

After having posted my question about how to link the two brightnesses together
on
[Microsoft forum](http://answers.microsoft.com/en-us/windows/forum/windows_10-other_settings/link-on-battery-and-plugged-in-screen-brightness/76b21adb-d741-4e66-b3d2-2adf616232b3?auth=1)
and on
[SuperUser](http://superuser.com/questions/1016560/link-on-battery-and-plugged-in-screen-brightness-together)
and not receiving a working answer, I decided to do the work on my own.

This simple program does the following:

- Wait for "brightness changed" event.
- If the event occurs, read the Plugged In and On Battery brightnesses of
  current power plan.
- If the two are different, set them to the same value.

Other
-----

### IF YOU DO NOT WANT TO BUILD THE SOURCE YOURSELF
Use this EXE I have compiled below. This was compiled .NET 4.5.2 on 64bit
system, so I do not know it would work on other configurations.
https://github.com/HubKing/LinkBrightness/blob/master/LinkBrightness/Compiled/LinkBrightness.exe <br />
Press the [Raw] button to download the EXE.

### TO BUILD FROM THE SOURCE CODE
use any IDE. I used SharpDevelop, because it is lightweighted. I set the .NET
level to 4.5.2, but it should also work on .NET 2.

### Licence
For the EXE and the source code, MIT Licence. Simply put, it is free for home
and work.
