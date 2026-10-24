"""Create the app's native text/shape icon. Pillow is only needed at build time."""
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

root = Path(__file__).resolve().parent
canvas = Image.new("RGBA", (256, 256))
draw = ImageDraw.Draw(canvas)
draw.rounded_rectangle((0, 0, 255, 255), radius=63, fill="#176D64")
font = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 174)
box = draw.textbbox((0, 0), "译", font=font)
draw.text(((256 - (box[2] - box[0])) / 2 - box[0], (256 - (box[3] - box[1])) / 2 - box[1] - 4), "译", fill="white", font=font)
canvas.save(root / "app.ico", sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
