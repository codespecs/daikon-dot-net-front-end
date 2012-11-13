dir = r'C:\Users\Kellen\Documents\daikon-dot-net-front-end\DaikonDotNetFrontEnd\DotNetFrontEndLauncher\bin\Debug'
dnfe = dir + r'\DotNetFrontEndLauncher.exe'
app = r'C:\Users\Kellen\Documents\daikon-dot-net-front-end\DotNetFrontEndTests\SikuliExample\SikuliExample.exe'
print 'hello'
openApp(dnfe + ' ' + app)
wait("iuwmanylicks.png")
click("iuwmanylicks.png")
numLicks = 5
type(str(numLicks))
type(Key.ENTER)
for i in range(numLicks+1):
    type(Key.ENTER)
