#!/usr/bin/python3
import base64
import json
import socket
import subprocess
import time
import urllib.parse as url
import urllib.request as net
import uuid
from pathlib import Path

STATE = Path.home() / '.gemini'
STATE.mkdir(mode=0o700, exist_ok=True)
PROFILE = STATE / 'outbound.json'

class NoRedirect(net.HTTPRedirectHandler):
    def redirect_request(self, *args, **kwargs):
        return None

def import_profile(link):
    if url.urlsplit(link).scheme != 'https':
        raise ValueError('HTTPS required')
    with net.build_opener(NoRedirect()).open(link, timeout=20) as response:
        raw = response.read(1048577)
    if len(raw) > 1048576:
        raise ValueError('Subscription too large')
    if b'vless://' not in raw:
        raw = b''.join(raw.split())
        raw = base64.urlsafe_b64decode(raw + b'=' * (-len(raw) % 4))
    for line in raw.decode('utf-8-sig').splitlines():
        try:
            u = url.urlsplit(line.strip())
            if u.scheme != 'vless' or not u.hostname or not u.port:
                continue
            q = dict(url.parse_qsl(u.query))
            sec = q.get('security')
            if sec not in ('tls', 'reality') or q.get('type', 'tcp') not in ('tcp', 'raw') or q.get('headerType', 'none') != 'none' or q.get('encryption', 'none') != 'none':
                continue
            stream = {'network': 'raw', 'security': sec}
            if sec == 'reality':
                stream['realitySettings'] = dict(password=q['pbk'], serverName=q['sni'], fingerprint=q.get('fp', 'chrome'), shortId=q.get('sid', ''), spiderX=q.get('spx', ''))
            else:
                tls = {'serverName': q.get('sni', u.hostname)}
                if q.get('alpn'):
                    tls['alpn'] = q['alpn'].split(',')
                if q.get('fp'):
                    tls['fingerprint'] = q['fp']
                stream['tlsSettings'] = tls
            user = dict(id=str(uuid.UUID(u.username)), encryption='none', flow=q.get('flow', ''))
            return dict(protocol='vless', streamSettings=stream, settings={'vnext': [dict(address=u.hostname, port=u.port, users=[user])]})
        except (ValueError, KeyError, TypeError, AttributeError):
            continue
    raise ValueError('No compatible profile')

def message(text):
    subprocess.run(['zenity', '--error', '--title=Gemini VM', '--text=' + text], check=False)

def start():
    while True:
        choice = subprocess.run(['zenity', '--list', '--title=Gemini VM', '--text=Gemini VM — select an action', '--column=Action', 'Open Gemini', 'Set subscription', 'Files / Gemini-Exchange', 'Shut down', '--width=460', '--height=300'], text=True, capture_output=True)
        action = choice.stdout.strip()
        if choice.returncode or action == 'Shut down':
            subprocess.run(['systemctl', 'poweroff'], check=False)
            return
        if action == 'Files / Gemini-Exchange':
            share = Path('/media/sf_Gemini-Exchange')
            downloads = Path.home() / 'Downloads'
            try:
                next(share.iterdir(), None)  # trigger kernel automount
                subprocess.Popen(['thunar', str(share)])
            except OSError:
                pass
            subprocess.Popen(['thunar', str(downloads)])
            continue
        if action == 'Set subscription' or not PROFILE.exists():
            secret = subprocess.run(['zenity', '--password', '--title=Paste HTTPS subscription'], text=True, capture_output=True)
            if secret.returncode:
                continue
            try:
                profile = import_profile(secret.stdout.strip())
                pending = PROFILE.with_suffix('.tmp')
                pending.write_text(json.dumps(profile))
                pending.replace(PROFILE)
            except Exception:
                message('Import failed. Expected HTTPS VLESS TCP Reality/TLS subscription without redirects.')
                continue
            if action == 'Set subscription':
                continue
        with socket.socket() as listener:
            listener.bind(('127.0.0.1', 0))
            port = listener.getsockname()[1]
        config = dict(log={'loglevel': 'none'}, inbounds=[dict(listen='127.0.0.1', port=port, protocol='http')], outbounds=[json.loads(PROFILE.read_text())])
        with subprocess.Popen(['/usr/local/bin/xray', 'run', '-config', 'stdin:'], stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) as proxy:
            proxy.stdin.write(json.dumps(config).encode())
            proxy.stdin.close()
            try:
                for _ in range(50):
                    if proxy.poll() is not None:
                        raise RuntimeError()
                    try:
                        with socket.create_connection(('127.0.0.1', port), timeout=.2):
                            break
                    except OSError:
                        time.sleep(.1)
                else:
                    raise RuntimeError()
                subprocess.run(['chromium', '--user-data-dir=' + str(STATE / 'chromium'), '--no-first-run', '--no-default-browser-check', '--disable-quic', '--force-webrtc-ip-handling-policy=disable_non_proxied_udp', '--host-resolver-rules=MAP * ~NOTFOUND, EXCLUDE 127.0.0.1', '--proxy-bypass-list=<-loopback>', f'--proxy-server=http://127.0.0.1:{port}', '--app=https://gemini.google.com/'], check=True)
            except (RuntimeError, subprocess.CalledProcessError):
                message('Could not start proxy or browser.')
            finally:
                if proxy.poll() is None:
                    proxy.terminate()
                    proxy.wait(timeout=5)

if __name__ == '__main__':
    start()
