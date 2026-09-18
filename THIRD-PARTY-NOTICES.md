# Third-party notices

This package contains material derived from the projects below. Their licences
are reproduced in full.

## Brotli reference implementation

https://github.com/google/brotli

Used for: the static dictionary, context lookup and transform tables under
`src/BrotliManagedFast/Internal/Tables`; the reference test vectors under
`src/BrotliManagedFast.Tests/TestData`; and algorithms in the encoder and
decoder that are ports of the reference code, each marked in a comment where it
appears.

The texts among those vectors (`alice29.txt`, `asyoulik.txt`, `lcet10.txt`,
`plrabn12.txt`) are the Canterbury corpus, vendored here as the reference
implementation vendors them. `lcet10.txt` carries a Project Gutenberg notice in
its own first lines.

Copyright (c) 2009, 2010, 2013-2016 by the Brotli Authors.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in
all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.  IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
THE SOFTWARE.
