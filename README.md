# LinkBrightness
This program links the brightnesses of Plugged In and On Battery together, so that if you change one, the other is also changed.

After having posted my question about how to link the two brightnesses together
on Microsoft forum (http://answers.microsoft.com/en-us/windows/forum/windows_10-other_settings/link-on-battery-and-plugged-in-screen-brightness/76b21adb-d741-4e66-b3d2-2adf616232b3?auth=1)
and on SuperUser (http://superuser.com/questions/1016560/link-on-battery-and-plugged-in-screen-brightness-together)
and not receiving a working answer, I decided to do the work on my own.

This simple program does the following:
Wait for "brightness changed" event. 
If the event occurs, read the Plugged In and On Battery brightnesses of current power plan.
If the two are different, set them to the same value.

##IF YOU DO NOT WANT TO BUILD THE SOURCE YOURSELF
Use this EXE I have compiled below. This was compiled .NET 4.5.2 on 64bit system, so I do not know it would work on other configurations.
https://github.com/HubKing/LinkBrightness/blob/master/LinkBrightness/Compiled/LinkBrightness.exe
Press the [Raw] button to download the EXE.

##TO BUILD FROM THE SOURCE CODE
use any IDE. I used SharpDevelop, because it is lightweighted. I set the .NET level to 4.5.2, but it should also work on .NET 2.
