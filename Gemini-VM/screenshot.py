import socket
import time
from pathlib import Path
from PIL import Image

def monitor(command):
    with socket.socket(socket.AF_UNIX) as client:
        client.connect('/tmp/gemini-monitor')
        client.recv(4096)
        client.sendall((command + '\n').encode())
        time.sleep(.3)

for _ in range(30):
    time.sleep(4)
    monitor('screendump /tmp/gemini-screen.ppm')
    path = Path('/tmp/gemini-screen.ppm')
    if path.exists():
        with Image.open(path) as screenshot:
            screenshot.save('output/kiosk.png')
        if screenshot.width >= 800 and screenshot.height >= 600:
            break
else:
    raise SystemExit('Guest did not reach graphical resolution')
monitor('quit')
