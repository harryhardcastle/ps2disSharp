**Description**

ps2dis# is a disassembler for PS2 binaries and a debugger for PCSX2. It is heavily based on the design of ps2dis and PCSX2dis. It uses PCSX2-MCP and PINE to communicate with a modified PCSX2.

**Requirements**
- In PCSX2, go to Settings > Advanced > Enable PINE Settings using slot 28011.
- To use breakpoints, you will need PCSX2-MCP: https://github.com/hkmodd/PCSX2-MCP (this repo also contains a precompiled PCSX2-MCP).

**General Features**
- Opens PS2 executables, memory dumps, ps2dis projects, and PCSX2dis projects.
- Opening a file, or attaching to PCSX2, automatically analyzes memory. Re-analyze the memory by clicking Analyzer > Invoke Analyzer.
- Imports labels from a PS2 ELF, binary, or PCSX2dis project.
- The Analyzer > Debug Window will show the status of the PINE and MCP connections along with a log that tracks both. The log is not active when the window is closed.

**PCSX2 Features**
- The Code Manager has 3 tabs: Codes, Patch Memory, and Search. The codes tab is for inputting raw hex codes similar to gameshark, and the codes are constantly written to memory. It supports 0, 1, 2, and D type codes. The Patch Memory tab is for patching memory. The codes in this tab are written to memory once, then cleared from the codes list. The Search tab is setup similar to Cheat Engine.
- Supports PC, read, and write breakpoints.
- Placing the access monitor on an address will show you what accesses the address. This can cause the game to slow down due to the constant pausing/resuming of PCSX2.
- Supports labeling dynamic objects such as a PlayerObject and storing them in a PCSX2dis project file.
#Example
0020446C:MyObject
0000:Field1
0004:Field2
0008:Field3

**Hotkeys**
- Go to Address: G
- Find: CTRL-F
- Labels: CTRL-G
- Save PCSX2dis project: CTRL-S
- Import Labels: CTRL-I
- Toggle Breakpoints sidebar: CTRL-B

**Credits**

1UP - Started the ps2dis# project  
gtlcpimp - Creator of Code Designer  
Hanimar - Creator of ps2dis  
LXShadow - Creator of PCSX2dis  
Sebastiano Gelmetti - Creator of PCSX2-MCP  
ChatGPT & Claude - Writing and breaking the code  
