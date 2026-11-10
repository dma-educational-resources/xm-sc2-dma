# SC2 DMA Radar

A StarCraft II radar that reads game memory over a DMA (FPGA) card from a second PC. Draws every unit, building and resource on the map with widgets for armies, production, income, cameras and more.

## How do I start

1. Open `xm-sc2-dma.sln` in Visual Studio.
2. Publish the `xm-sc2-dma` project (Release, x64) to a folder.
3. Run `xm-sc2-dma.exe` from that folder.
4. Start StarCraft II on the gaming PC - the radar attaches by itself.

## Memory writes

A few memory writes (Reveal Map, Unit Sight, Share Enemy Vision, Camera Zoom, Ultrawide, Observer View, Fast Win) live behind the Mem Writes panel's master switch. Most change the synced game state, so use them only vs AI, in the campaign, or offline arcade - they will drop you from online matches.

## Preview
<img width="1995" height="1126" alt="image" src="https://github.com/user-attachments/assets/5b7c7d9c-91ec-487c-9b51-747644aec83d" />
