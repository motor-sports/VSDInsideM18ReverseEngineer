# VSDInsideM18ReverseEngineer
VSD Software for RaspberryPi that enables the device to communicate wirelessly with windows companion app, which allows user to bind button presses to game keybinds

# Process
Started by downloading the official windows app, then sniffing the packets between the PC and the HID device with WireShark and USBPCAP
This allowed me to view the raw hex packets between the two devices while also finding HID vendor ID and product ID
With these packets I was able to completely reverse engineer the lighting, screen display, buttons, and detect keypresses from the device

# Python file
Simplest file of the bunch - does absolutely nothing but take input TCP data from PC and directly send it to USB M18 device - then takes input USB data from M18 device and sends it over TCP to the companion app
(in essence, it creates a usb connection over TCP wirelessly)

# Windows Companion App
All the reverse engineering lives here, all commands are managed here which makes the app easy to update as the only device logic is contained in the CS files
The code for directly controlling the device over USB is the exact same for control over TCP wirelessly except for the send_packet() function
