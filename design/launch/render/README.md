# Rendering the film to video

The film is `../film.html`: every frame is computed from the time, so a headless Chrome can be asked for
each frame in turn.

```
npm init -y && npm i puppeteer-core          # once, in this folder (uses the Chrome already installed)
node shoot.js render frames 60               # 1638 JPEG frames, about 7 minutes
ffmpeg -framerate 60 -i frames/%05d.jpg -c:v libx264 -crf 15 -preset slow -pix_fmt yuv420p -movflags +faststart ../out/island-launch-silent.mp4
node shoot.js stills stills 2.6,10.5,22.3    # single moments as PNG
```

To add sound afterwards: `ffmpeg -i ../out/island-launch-silent.mp4 -i mix.wav -c:v copy -c:a aac -b:a 256k -shortest ../out/island-launch.mp4`
