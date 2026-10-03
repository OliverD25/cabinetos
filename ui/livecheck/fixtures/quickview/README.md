# Quick View fixtures

`photo.heic` (3.9 KB, 800 x 600) is the HEIC row of the live check's Quick View
section: the Image Viewer asks Windows to draw it (`quickview-render`). The
live check uses it only when the machine has Windows' HEIF codec (the HEIF
Image Extensions of the Microsoft Store), and prints a line saying it skipped
the row otherwise.

Made once with Python's Pillow 12 and pillow-heif 1.8 (`img.save(path, format="HEIF", quality=30)`
on a colour gradient with a white frame and a line of text).
