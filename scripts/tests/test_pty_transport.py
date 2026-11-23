"""Exercise the local PTY service against an isolated RFC2217 loopback."""

import os
import json
import queue
import select
import signal
import socket
import socketserver
import subprocess
import sys
import tempfile
import termios
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path
from types import SimpleNamespace
from serial.rfc2217 import PortManager

app = Path(sys.argv[-1]).resolve(strict=True)
command = [*sys.argv[1:-1], str(app)]
connections = queue.Queue()

class Connection(socketserver.BaseRequestHandler):
    def setup(self):
        self.lock = threading.Lock()
    def write(self, data):
        with self.lock:
            self.request.sendall(data)
    def handle(self):
        self.serial = SimpleNamespace(
            baudrate=115200, bytesize=8, parity='N', stopbits=1,
            xonxoff=False, rtscts=False, dtr=False, rts=False,
            break_condition=False, cts=False, dsr=False, ri=False, cd=False,
            reset_input_buffer=lambda: None, reset_output_buffer=lambda: None)
        self.manager = PortManager(self.serial, self)
        connections.put(self)
        while True:
            data = self.request.recv(8192)
            if not data:
                return
            decoded = b''.join(self.manager.filter(data))
            if decoded:
                self.write(b''.join(self.manager.escape(decoded)))

def wait_for(predicate, seconds=9):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        if predicate():
            return
        time.sleep(.05)
    raise AssertionError("timed out waiting for " + str(predicate))

def read_exact(fd, length):
    received = b''
    deadline = time.monotonic() + 8
    while len(received) < length:
        if not select.select([fd], [], [], max(0, deadline-time.monotonic()))[0]:
            raise AssertionError(f'PTY received {received.hex()}')
        received += os.read(fd, length-len(received))
    return received

with tempfile.TemporaryDirectory(prefix='xserial-vcom-pty-') as home:
    with socketserver.ThreadingTCPServer(('127.0.0.1', 0), Connection) as remote:
        remote.daemon_threads = True
        threading.Thread(target=remote.serve_forever, daemon=True).start()
        with socket.socket() as probe:
            probe.bind(('127.0.0.1', 0))
            service_port = probe.getsockname()[1]
        env = os.environ | {
            'VCOMTUNNEL_HOME': home,
            'VCOMTUNNEL_SERVICE_URL': f'http://127.0.0.1:{service_port}',
            'Logging__LogLevel__Microsoft': 'Warning',
        }
        visible = Path(home) / 'port'
        api = f'http://127.0.0.1:{service_port}'
        def request(path, method='GET', body=None):
            data = json.dumps(body).encode() if body is not None else None
            req = urllib.request.Request(api+path, data=data, method=method,
                headers={'Content-Type':'application/json'})
            with urllib.request.urlopen(req, timeout=18) as response:
                return json.load(response)
        def start_service(log):
            proc = subprocess.Popen([*command, '--console'], env=env,
                stdout=log, stderr=subprocess.STDOUT, cwd=app.parent)
            def ready():
                if proc.poll() is not None:
                    raise AssertionError(f'PTY service exited {proc.returncode}')
                try:
                    return request('/api/status')['configPath'] == home+'/config.json'
                except OSError:
                    return False
            try:
                wait_for(ready)
            except Exception:
                proc.terminate()
                proc.wait(12)
                raise
            return proc
        def status(mapping_id='loop'):
            return next((t['state'] for t in request('/api/status')['tunnels']
                if t['id'] == mapping_id), None)
        def stop_service(proc):
            if proc.poll() is None:
                proc.send_signal(signal.SIGTERM)
                assert proc.wait(12) == 0
        mapping = {'id':'loop', 'name':'loopback fixture', 'backend':'pty',
            'visiblePort':str(visible), 'backingPort':None, 'host':'127.0.0.1',
            'port':remote.server_address[1], 'protocol':'rfc2217',
            'autoStart':True, 'restartOnFailure':True,
            'trafficLog':{'enabled':True, 'format':'rawBinary'}}
        with open(Path(home) / 'service.log', 'w') as log:
            proc = start_service(log)
            try:
                invalid = dict(mapping, visiblePort='relative')
                try:
                    request('/api/mappings', 'PUT', [invalid])
                    raise AssertionError('relative PTY path was accepted')
                except urllib.error.HTTPError as e:
                    assert e.code == 400
                assert request('/api/mappings','PUT',[mapping])['saved'] == 1
                assert request('/api/mappings')[0]['backend'] == 'pty'
                result = request('/api/mappings/loop/start', 'POST')
                assert result['state'] == 'running', result
                conn = connections.get(timeout=4)
                assert visible.is_symlink()
                assert os.path.realpath(visible).startswith(('/dev/pts/', '/dev/ttys'))
                fd = os.open(visible, os.O_RDWR | os.O_NOCTTY)
                try:
                    payload = b'\x00\x11\x13\xff\r\nXSerial\x80'
                    os.write(fd, payload)
                    assert read_exact(fd, len(payload)) == payload
                    remote_data = b'remote\xff\x00\r\n'
                    conn.write(b''.join(conn.manager.escape(remote_data)))
                    assert read_exact(fd, len(remote_data)) == remote_data
                    attrs = termios.tcgetattr(fd)
                    attrs[4] = attrs[5] = termios.B57600
                    termios.tcsetattr(fd, termios.TCSANOW, attrs)
                    wait_for(lambda: conn.serial.baudrate == 57600)
                    wait_for(lambda: request('/api/traffic-logs')[0].get('bytesWritten', 0) > 0)
                finally:
                    os.close(fd)
                print('PASS PTY creation, binary transport in both directions, baud updates and capture')
                reopened = os.open(visible, os.O_RDWR | os.O_NOCTTY)
                try:
                    os.write(reopened,b'reopen')
                    assert read_exact(reopened,6)==b'reopen'
                finally:
                    os.close(reopened)
                conn.request.shutdown(socket.SHUT_RDWR)
                connections.get(timeout=10)
                wait_for(lambda: status() == 'running')
                fd = os.open(visible, os.O_RDWR | os.O_NOCTTY)
                try:
                    os.write(fd, b'after-reconnect')
                    assert read_exact(fd, 15) == b'after-reconnect'
                finally:
                    os.close(fd)
                print('PASS slave reopen and RFC2217 reconnect')
                assert request('/api/mappings/loop/stop','POST')['state'] == 'stopped'
                assert not visible.is_symlink()
                visible.write_text('existing file')
                result = request('/api/mappings/loop/start','POST')
                assert result['state'] == 'faulted', result
                assert visible.read_text() == 'existing file'
                visible.unlink()
                print('PASS stopped mapping removes owned link and preserves an existing path')
            finally:
                stop_service(proc)

            proc = start_service(log)
            try:
                assert request('/api/mappings')[0]['visiblePort'] == str(visible)
                wait_for(lambda: status() == 'running')
                assert visible.is_symlink()
                assert request('/api/mappings','PUT',[])['saved'] == 0
                assert not visible.is_symlink()
                assert request('/api/mappings') == []
                print('PASS persisted mapping, auto-start after restart and mapping deletion')
                assert request('/api/mappings','PUT',[mapping])['saved'] == 1
                assert request('/api/mappings/loop/start','POST')['state'] == 'running'
                assert visible.is_symlink()
                stop_service(proc)
                assert not visible.is_symlink()
                print('PASS graceful service shutdown removes the PTY link')
            finally:
                stop_service(proc)
        print(f'{sys.platform} PTY integration PASS')
