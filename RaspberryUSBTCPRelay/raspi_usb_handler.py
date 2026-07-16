#!/usr/bin/env python3

"""
VSDM18 USB Relay

TCP <-> USB packet relay.

Behavior:
- When a TCP peer connects, open and initialize the USB device.
- When the TCP peer disconnects, close the USB device.
- When a new peer connects later, reconnect and reinitialize USB.
- When the peer disconnects, stop and restart this process.
"""

import usb.core
import usb.util
import socket
import threading
import time
import sys
import os
import subprocess

# ============================================================================
# Configuration
# ============================================================================

CHROMIUM_PROC = None

def open_chromium_for_peer(peer_ip):
    global CHROMIUM_PROC

    if CHROMIUM_PROC and CHROMIUM_PROC.poll() is None:
        return

    url = f"http://{peer_ip}:8888/"

    print(url)

    env = os.environ.copy()
    env["DISPLAY"] = ":0"

    subprocess.Popen(["unclutter", "-idle", "0", "-root"], env=env)

    CHROMIUM_PROC = subprocess.Popen([
        "chromium-browser",
        "--noerrdialogs",
        "--disable-infobars",
        "--disable-session-crashed-bubble",
        "--disable-gpu",
        "--start-maximized",
        "--kiosk",
        url
    ], env=env)

def close_chromium():
    global CHROMIUM_PROC

    if CHROMIUM_PROC and CHROMIUM_PROC.poll() is None:
        try:
            CHROMIUM_PROC.terminate()
            CHROMIUM_PROC.wait(timeout=5)
        except:
            try:
                CHROMIUM_PROC.kill()
            except:
                pass

    CHROMIUM_PROC = None

VID = 0x5548
PID = 0x1000

PACKET_SIZE = 1024

TCP_HOST = "0.0.0.0"
TCP_PORT = 5000

# ============================================================================
# Process restart
# ============================================================================

def restart_self():
    print("[SYSTEM] Restarting relay process...")
    sys.stdout.flush()
    sys.stderr.flush()
    os.execv(sys.executable, [sys.executable] + sys.argv)

# ============================================================================
# USB Device
# ============================================================================

class USBDevice:
    def __init__(self):
        self.dev = None
        self.ep_out = None
        self.ep_in = None
        self.connected = False
        self.lock = threading.Lock()

    def pad_packet(self, data):
        packet = bytearray(data)

        if len(packet) > PACKET_SIZE:
            packet = packet[:PACKET_SIZE]
        elif len(packet) < PACKET_SIZE:
            packet.extend([0] * (PACKET_SIZE - len(packet)))

        return bytes(packet)

    def open(self):
        with self.lock:
            if self.connected:
                return

            print(f"[USB] Searching VID={VID:04X} PID={PID:04X}")
            self.dev = usb.core.find(idVendor=VID, idProduct=PID)

            if self.dev is None:
                raise Exception("USB device not found")

            print("[USB] Device found")

            try:
                if self.dev.is_kernel_driver_active(0):
                    self.dev.detach_kernel_driver(0)
            except:
                pass

            usb.util.claim_interface(self.dev, 0)

            cfg = self.dev.get_active_configuration()
            intf = cfg[(0, 0)]

            self.ep_out = usb.util.find_descriptor(
                intf,
                custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)
                == usb.util.ENDPOINT_OUT
            )

            self.ep_in = usb.util.find_descriptor(
                intf,
                custom_match=lambda e: usb.util.endpoint_direction(e.bEndpointAddress)
                == usb.util.ENDPOINT_IN
            )

            if self.ep_out is None:
                raise Exception("USB OUT endpoint not found")

            print(f"[USB] OUT endpoint 0x{self.ep_out.bEndpointAddress:02X}")

            if self.ep_in:
                print(f"[USB] IN endpoint 0x{self.ep_in.bEndpointAddress:02X}")
            else:
                print("[USB] WARNING: IN endpoint missing")

            self.connected = True

        self.initialize_device()
        print("[USB] Connected")

    def initialize_device(self):
        commands = [
            "43 52 54 00 00 43 4C 45 00 00 00 FF",
            "43 52 54 00 00 48 41 4E",
        ]

        print("[USB] Initializing device...")

        for cmd in commands:
            self.send_command(cmd)
            time.sleep(0.05)

    def write(self, data):
        with self.lock:
            if not self.connected:
                raise RuntimeError("USB device not connected")

            packet = self.pad_packet(data)

            self.dev.write(
                self.ep_out.bEndpointAddress,
                packet,
                timeout=5000
            )

    def send_command(self, command):
        if isinstance(command, str):
            command = bytes.fromhex(command)

        self.write(command)

        print("[USB CMD] " + " ".join(f"{x:02X}" for x in command))

    def read(self):
        with self.lock:
            if not self.connected or self.ep_in is None:
                return None

            dev = self.dev
            ep_addr = self.ep_in.bEndpointAddress

        try:
            data = dev.read(ep_addr, PACKET_SIZE, timeout=100)
            return bytes(data)
        except usb.core.USBError:
            return None
        except:
            return None

    def close(self):
        with self.lock:
            if self.dev is None:
                self.connected = False
                self.ep_out = None
                self.ep_in = None
                return

            try:
                usb.util.dispose_resources(self.dev)
            except:
                pass

            try:
                usb.util.release_interface(self.dev, 0)
            except:
                pass

            self.dev = None
            self.ep_out = None
            self.ep_in = None
            self.connected = False


# ============================================================================
# TCP Relay
# ============================================================================

class TCPRelay:
    def __init__(self, usb_device):
        self.usb = usb_device
        self.server = None
        self.client = None
        self.running = False
        self.client_lock = threading.Lock()
        self.session_id = 0

    def start(self):
        self.server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        self.server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        self.server.bind((TCP_HOST, TCP_PORT))
        self.server.listen(1)

        self.running = True

        print(f"[TCP] Listening on {TCP_HOST}:{TCP_PORT}")

        threading.Thread(target=self.accept_loop, daemon=True).start()

    def accept_loop(self):
        while self.running:
            try:
                client, addr = self.server.accept()
                peer_ip = addr[0]
                print(f"[TCP] Client connected: {addr}")
                open_chromium_for_peer(peer_ip)

                client.settimeout(0.5)

                with self.client_lock:
                    self.client = client
                    self.session_id += 1
                    sid = self.session_id

                print(f"[TCP] Client connected: {addr}")

                try:
                    self.usb.open()
                except Exception as e:
                    print(f"[USB ERROR] {e}")
                    self._close_client(client)
                    with self.client_lock:
                        if self.client is client:
                            self.client = None
                    continue

                threading.Thread(
                    target=self.tcp_to_usb,
                    args=(client, sid),
                    daemon=True
                ).start()

                threading.Thread(
                    target=self.usb_to_tcp,
                    args=(client, sid),
                    daemon=True
                ).start()

            except OSError:
                time.sleep(0.5)

    def tcp_to_usb(self, client, sid):
        try:
            while self.running:
                with self.client_lock:
                    if self.client is not client or sid != self.session_id:
                        return

                data = b""

                while len(data) < PACKET_SIZE:
                    try:
                        chunk = client.recv(PACKET_SIZE - len(data))
                    except socket.timeout:
                        continue

                    if not chunk:
                        print("[TCP] Peer disconnected")
                        return

                    data += chunk

                self.usb.write(data)

                print("[TCP -> USB] " + " ".join(f"{x:02X}" for x in data[:16]))

        except Exception as e:
            print(f"[TCP->USB ERROR] {e}")
        finally:
            self.disconnect_client(client)

    def usb_to_tcp(self, client, sid):
        try:
            while self.running:
                with self.client_lock:
                    if self.client is not client or sid != self.session_id:
                        return

                data = self.usb.read()

                if data:
                    try:
                        client.sendall(data)
                        print("[USB -> TCP] " + " ".join(f"{x:02X}" for x in data[:16]))
                    except OSError:
                        print("[TCP] Peer disconnected while sending")
                        return
                else:
                    time.sleep(0.01)

        except Exception as e:
            print(f"[USB->TCP ERROR] {e}")
        finally:
            self.disconnect_client(client)

    def _close_client(self, client):
        try:
            client.shutdown(socket.SHUT_RDWR)
        except:
            pass

        try:
            client.close()
        except:
            pass

    def disconnect_client(self, client=None):
        should_close_usb = False
        should_restart = False

        with self.client_lock:
            if client is None:
                client = self.client

            if client is not None and self.client is client:
                self.client = None
                should_close_usb = True
                should_restart = True

        if client:
            self._close_client(client)

        if should_close_usb:
            close_chromium()
            self.usb.close()
            print("[USB] Disconnected")

        if should_restart:
            self.stop()
            time.sleep(1)
            restart_self()

    def stop(self):
        self.running = False
        self.disconnect_client()

        if self.server:
            try:
                self.server.close()
            except:
                pass


# ============================================================================
# Main
# ============================================================================

def main():
    usb_device = None
    relay = None

    try:
        print("=" * 70)
        print("VSDM18 USB Relay")
        print("=" * 70)

        usb_device = USBDevice()
        relay = TCPRelay(usb_device)
        relay.start()

        print("[READY] Relay running")

        while True:
            time.sleep(1)

    except KeyboardInterrupt:
        print("[SHUTDOWN] User exit")

    except Exception as e:
        print(f"\n[ERROR] {e}")

        if "No such device" in str(e):
            print("[SYSTEM] USB device disappeared. Restarting...")
            time.sleep(2)
            restart_self()
        else:
            import traceback
            traceback.print_exc()

    finally:
        if relay:
            relay.stop()
        if usb_device:
            usb_device.close()
        close_chromium()


if __name__ == "__main__":
    main()
