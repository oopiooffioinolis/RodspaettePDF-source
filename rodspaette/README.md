# Rødspætte PDF

A light PDF reader for Windows 10 and 11.

## Install

1. Install the **.NET 8 SDK** (free): https://dotnet.microsoft.com/download/dotnet/8.0. Pick "SDK 8.0.x → Windows x64".
2. Double-click **build.cmd**.

It builds the app, copies it to `%LOCALAPPDATA%\Programs\Rødspætte PDF`, adds it to the Start menu and opens it. You don't need admin rights. Run `build.cmd` again after changing the code.

To make it your default PDF app, go to Settings → Apps → Default apps → Rødspætte PDF → .pdf. It also appears under "Open with". Windows doesn't let apps make themselves the default.

## Keys

These are the defaults. Change any of them under Settings (gear) → **Shortcuts…**: click a shortcut and press the new keys (Backspace removes it). A key taken from another command leaves that command without one.

| Keys | Action |
|---|---|
| Ctrl+H | Retract the pinned side panel; press again to bring it back |
| Ctrl+O / Ctrl+W | Open file / close tab |
| Ctrl+Tab, Ctrl+Shift+Tab | Next / previous tab (goes round all tabs) |
| Ctrl+Q | Last-used tab: switches between the two most recent tabs |
| Ctrl+S / Ctrl+Shift+S | Save / Save As |
| Ctrl+P | Print |
| Ctrl+F, F3, Shift+F3 | Search (Ctrl+F again closes it), next result, previous result |
| Ctrl+L | Look up the selected text with Google AI Mode (with nothing selected: ask anything); press again to close it |
| Ctrl+D | Add or remove a bookmark on the current page |
| Ctrl+Z | Undo the last markup |
| Ctrl+C | Copy the selected text |
| Ctrl + mouse wheel, Ctrl+plus, Ctrl+minus | Zoom |
| Ctrl+0 | Fit width |
| Delete | Delete the selected note, text box or drawing |
| Esc | Close the open editor, search, Look up or tool; leave full screen or the home page (always Esc) |

Page numbers…, Full screen, Home page and each markup tool have no key until you give them one.

## Where things are

- **Title bar**: the house on the left shows the home page (the big logo) without closing anything; click it again, pick a tab or press Esc to go back. On the right: a no-Wi-Fi sign while there is no internet, search, settings, appearance, print, and the menu button (three lines) with Save, Save As, Print, Open, Rename, Show in folder, Page numbers… and Close tab.
- **Side panels** (both on the left): two thin bars on the left edge. The top bar slides in Contents (the PDF's chapters), Bookmarks and Markup; search results appear there too. The bottom bar slides in Tabs and recent files; drag a tab up or down the list to reorder the tabs (the taskbar previews follow). Each panel has a pin; only one can be pinned at a time, and the other stays one touch away on its bar. A pinned panel stays exactly where it floated, with a strip of its own colour along the window's left edge.
- **Bottom toolbar** (always shown; drag it by its background to move it anywhere):
  - The arrow-to-the-line button moves the toolbar back to the bottom centre.
  - The full-screen button turns full screen on (blue) and off.
  - Select, Highlight, Underline, Strikethrough, Pen, Note and Text box. Right-click a tool to change its colour.
  - On the home page only the first two buttons (back to the bottom, full screen) are shown; the rest folds away towards the middle and comes back with a document.
  - Undo, zoom, and the page number box. It shows what the page is called; when the PDF has its own page numbers, the page's place in the file is beside it, like "(14 / 300)". Type a page number as printed (14, xiv, A-3) or "(14)" for the 14th page of the file.
- **Right-click menu**: what fits the spot you clicked (copy, look up, mark, comment, note…), then tools and colour, undo, bookmark, save, print and Update request. It grows to the height of the screen, so it doesn't scroll.
- **Appearance** (half-circle button): Light, Dark or Green, and "Tint PDF page".
- **Settings** (gear): Function dial…, **Mouse behaviour…** (scroll sensitivity, with a box to try it in), Shortcuts…, Open where I left off, **Tool bar transparency** (the toolbar and Look up fade a little while the mouse is elsewhere; typing in them keeps them solid until a second after the last key), **Undocked mode** (see Look up) and **Update app** (rebuilds and installs the app from this source folder; the version is shown below it).
- **Taskbar**: hovering the icon shows a preview of every tab. On first start the app offers to switch on the Windows setting that makes one click open your last tab. It applies to all apps and takes effect after you sign out and back in.

## Look up

Select some text and press **Ctrl+L** (or right-click the selection → **Look up**) to ask Google about it in AI Mode. The bottom toolbar's top edge rises to make room and the answer appears in it once it has. With nothing selected, Ctrl+L opens AI Mode's own start page. You can also put **Look up** on the function dial (Settings → Function dial…). Without internet, Look up shows a "Connection Lost" fish instead.

- Only Google's page is shown: ask follow-up questions in Google's own box. **Esc** or a click anywhere else closes it (that click does nothing else, except a right-click, which still opens its menu).
- When the toolbar has been dragged near the top of the window, its bottom edge drops instead.
- **Undocked mode** (Settings): the page opens beside the mouse as a card, like a right-click menu. Drag its top rim to move it and its corner or edges to resize it. The size and the mode are remembered.
- Links that lead away from Google open in your normal browser.
- It runs in WebView2, the Edge engine that comes with Windows 11 and most Windows 10 PCs, with its own profile in `%LOCALAPPDATA%\Rødspætte PDF\WebView2`. Google's cookie choice (and a Google sign-in, if you sign in there) is kept between uses.
- AI Mode needs a Google region where it is offered; Denmark is one.

## Comments on highlights

Right-click a highlight (or an underline, strikethrough or drawing) → **Add comment**. The comment is stored in the PDF the standard way (the annotation's `/Contents`), so Acrobat, Edge, Preview and other readers show it as that highlight's comment, and comments made there show up here.

- A small speech bubble marks every highlight with a comment. Hover the highlight to read it; click the bubble (or right-click → **Edit comment**) to change or remove it.
- The Markup panel shows each comment under its highlighted text.
- **Pictures**: with the Highlight tool, drag across a picture (anywhere away from text) to draw a rectangular highlight over it. It takes a comment like any other highlight. This also works on scanned pages. The pointer tells the two apart: an I-beam over text, a cross where a box will be drawn.
- **Text boxes**: Enter finishes typing (Shift+Enter starts a new line). They stay editable (Select or Text box tool): drag one to move it, drag the handle on either side to change its width (the text wraps again and the height follows), double-click it to change the text (empty it to delete the box).

## Page numbers

Many PDFs number their pages differently from their place in the file: a book's page 1 might be the file's 14th page. ☰ → **Page numbers…** fixes that for good, by writing page numbers into the PDF (its "page labels", which Acrobat and most PDF apps use; web browsers' viewers usually still show 1, 2, 3).

1. Choose the pages to change (it starts with the run of pages you are in), then say what one of them reads, e.g. "Page 14 reads 1". **Shift** shows the same thing as an offset (−13) and can be typed instead. Pick the style: 1, 2, 3 / i, ii, iii / I, II, III / a, b, c / A, B, C. The numbers count on from that page in both directions.
2. Every page must end up with exactly one number that no other page has. If the change would leave pages with a number below 1, with no number, or with the same number as other pages, the app asks, one run of pages at a time, how to number them: carry on from the page before, i ii iii, I II III, a b c, A B C, 1 2 3, or 1 2 3 with a prefix (like A-1). Choices that would clash with pages already settled are greyed out. Back goes to the previous question.
3. Check the new numbering and click **Apply**. It is saved into the PDF with your next save (or right away with Autosave). **Plain 1, 2, 3 …** removes a PDF's page numbers.

Bookmarks, search results, the markup list and the page box all use the new numbers. Encrypted PDFs (protected by their maker) can't be changed.

## How saving works

- **Highlights, underlines, strikethroughs, pen drawings, notes, text boxes and comments** are saved into the PDF as standard annotations, so Acrobat, Edge and phone readers show them too.
- **Bookmarks** are saved into the PDF's outline, in a folder called "Bookmarks" at the end of the chapter list. Other readers show that folder.
- **Page numbers** are saved as the PDF's page labels, added to the end of the file so the rest of it stays exactly as it was.
- **Text recognition (OCR)** uses the engine built into Windows. It runs by itself in the background on scanned pages that have no text, so you can search and highlight them. The recognised text is not written into the file. Windows uses the OCR languages installed under Settings → Time & language → Language.

## Project layout

```
src/Rodspaette.Core   PDF engine: PDFium bindings, rendering, text, search, annotations, saving, bookmark and page-number writers
src/Rodspaette.App    The Windows app (WPF)
tests/                Engine tests (run with: dotnet run --project tests/Rodspaette.CoreTests -- tests/data tests/out)
native/win-x64        pdfium.dll (PDFium 159, from pypdfium2) and its licences
```

PDFium is © The PDFium Authors, licensed under BSD-3-Clause and Apache-2.0. See `native/licenses`.

## Saving

The Save button saves now. Its arrow opens Save options: **Autosave** saves each change to the file a moment after you make it, and Save As….
