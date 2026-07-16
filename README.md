# VSDInsideM18ReverseEngineer
VSD Software for RaspberryPi that enables the device to communicate wirelessly with windows companion app, which allows user to bind button presses to game keybinds

# Why
I love simracing, however I have a profound lack of buttons to control car functions in games, so I looked to buy a button box.
Fun fact, these boxes can run you about $300
So, I studied 2 options, 
1. DIY button box
2. Stream Deck

I went with option 2, as I wanted to be able to change the button labels on demand without having to mess with stickers
Now I had to decide between a real Stream deck $150 for 10 Screen backed buttons or the VSDInside M18 $40 with 15 screen backed buttons
Obviously, I went with the M18
Next issue - shortage of ports / portability
I wanted my button box to be attatched to my sim rig, and I had no more front-facing USB ports on my PC, so I decided to use the Raspberry Pi 4B already attatched to my sim rig (acting as a virtual dashboard)
in the end, I ended up just coding my own interface/relay layer in python, and my own companion app on visual studio

My goal was to make something very expensive, but so practical (button boxes), accessible for more simracers to use, which I believe I succeeded in

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
