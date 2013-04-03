import os

dnfe = os.environ['DNFE_OUT'] + r'\DotNetFrontEndLauncher.exe'
app = 'SikuliExample.exe'
openApp(dnfe + ' ' + app)
wait("iuwmanylicks.png")
click("iuwmanylicks.png")
numLicks = 5
type(str(numLicks))
type(Key.ENTER)
for i in range(numLicks+1):
    type(Key.ENTER)
