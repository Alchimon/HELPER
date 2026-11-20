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

serial = Path('/tmp/gemini-serial')
for _ in range(60):
    if serial.exists() and b'GEMINI_KIOSK_READY' in serial.read_bytes():
        break
    time.sleep(3)
else:
    raise SystemExit('Guest did not display the Gemini kiosk window')
time.sleep(2)
monitor('screendump /tmp/gemini-screen.ppm')
with Image.open('/tmp/gemini-screen.ppm') as screenshot:
    screenshot.save('output/kiosk.png')
monitor('quit')
