# Yib for Windows — แผนงานสร้างแอป (สำหรับป้อนให้ Claude Code)

> แอป Windows จิ๋วบน System Tray — เขย่าเมาส์ → วงเลขเด้งขึ้น → เลือกจำนวนไฟล์ล่าสุด → แปะ (Ctrl+V) เข้าหน้าต่างที่เปิดอยู่ให้อัตโนมัติ
> ดัดแปลงจาก Yib (macOS, Swift) ของ PEESAMAC มาเป็นฝั่ง Windows

---

## 0. สรุปสำหรับ Claude Code (อ่านอันนี้ก่อน)

สร้างแอป Windows desktop ด้วย **C# .NET 8 + WinForms** ที่:
1. รันอยู่เบื้องหลังบน System Tray ไม่มีหน้าต่างหลัก
2. ดักการ "เขย่าเมาส์" (สะบัดซ้าย-ขวาเร็วๆ) ด้วย low-level mouse hook
3. เมื่อตรวจพบการเขย่า → แสดง **การ์ดวงเลขแบบโทรศัพท์หมุน (rotary dial)** ตรงตำแหน่งเคอร์เซอร์
4. ผู้ใช้ **เลื่อนเมาส์เล็งเลข** รอบวง (เลขที่ใกล้สุดไฮไลต์ พร้อมเส้นชี้ + ตัวเลขใหญ่กลางจอ + preview) แล้ว **คลิกยืนยัน** → ดึงไฟล์ล่าสุด N ไฟล์จาก Downloads (หรือ Desktop) → ใส่ลง clipboard แบบ CF_HDROP (เหมือนคัดลอกไฟล์จาก File Explorer) → ส่ง Ctrl+V
5. ตั้งค่าได้ผ่านเมนู tray: ความไวเขย่า 3 ระดับ / สลับ Downloads⇄Desktop / เปิด-ปิดเสียง
6. จำค่า settings ลงไฟล์ JSON ที่ `%APPDATA%\Yib\settings.json`

**สำคัญ:** ให้ build เป็น single-file self-contained .exe ด้วย NativeAOT (หรือ PublishSingleFile ถ้า AOT มีปัญหากับ WinForms) เพื่อให้ผู้ใช้ดับเบิลคลิกรันได้เลยไม่ต้องลง .NET runtime

> **อ้างอิงหน้าตา UI:** มีไฟล์ demo `yib-demo.html` (เปิดในเบราว์เซอร์ได้) ที่จำลองหน้าตาและพฤติกรรมของวงเลขที่ต้องการไว้แล้ว — ให้ยึดตามนั้นเป็นต้นแบบของ `DialForm.cs`

---

## 1. Tech Stack

| ส่วน | เทคโนโลยี |
|---|---|
| ภาษา | C# (.NET 8) |
| UI framework | WinForms (เบากว่า WPF สำหรับงาน tray + overlay เล็กๆ) |
| Mouse hook | Win32 `SetWindowsHookEx` กับ `WH_MOUSE_LL` (P/Invoke) |
| Tray icon | `NotifyIcon` (มากับ WinForms) |
| วงเลข overlay | `Form` แบบ borderless + topmost + โปร่งแสง วาดด้วย GDI+ (`System.Drawing`) |
| Clipboard ไฟล์ | `Clipboard.SetFileDropList()` (.NET มีให้แล้ว ไม่ต้อง P/Invoke เอง) |
| ส่งคีย์ Ctrl+V | Win32 `SendInput` (P/Invoke) — เสถียรกว่า `SendKeys` |
| เสียงคลิก | `Console.Beep()` หรือ `SystemSounds` |
| Settings | `System.Text.Json` เขียน/อ่านไฟล์ JSON |
| Build | `dotnet publish` → single-file exe |

**Dependencies เสริม:** ไม่จำเป็น ใช้ของ built-in .NET ได้หมด (เป้าหมายคือเบาเหมือนต้นฉบับ)

---

## 2. โครงสร้างโปรเจกต์

```
Yib/
├── Yib.csproj
├── Program.cs                 // entry point, ApplicationContext, ผูก tray
├── TrayApplicationContext.cs  // จัดการ tray icon + เมนู + lifecycle
├── MouseShakeDetector.cs      // low-level mouse hook + อัลกอริทึมตรวจเขย่า
├── DialForm.cs                // วงเลข overlay (borderless topmost form)
├── FilePicker.cs              // ดึงไฟล์ล่าสุดจากโฟลเดอร์ + เรียงตามเวลา
├── ClipboardPaster.cs         // ใส่ไฟล์ลง clipboard + ส่ง Ctrl+V (SendInput)
├── Settings.cs                // โมเดล settings + โหลด/เซฟ JSON
├── NativeMethods.cs           // รวม P/Invoke declarations ทั้งหมด
└── Resources/
    └── yib.ico                // ไอคอน tray (ทำเองหรือใช้ placeholder ก่อน)
```

---

## 3. รายละเอียดแต่ละโมดูล (spec ให้ Claude Code implement)

### 3.1 `Settings.cs`
- โมเดล: `Sensitivity` (enum: Loose/Medium/Tight), `SourceFolder` (enum: Downloads/Desktop), `SoundEnabled` (bool)
- ค่า default: Medium, Downloads, SoundEnabled=true
- เมธอด `Load()` อ่านจาก `%APPDATA%\Yib\settings.json` (ถ้าไม่มีไฟล์ → คืน default), `Save()` เขียนกลับ
- ใช้ `System.Text.Json`

### 3.2 `NativeMethods.cs`
รวม P/Invoke ที่ต้องใช้:
- `SetWindowsHookEx`, `UnhookWindowsHookEx`, `CallNextHookEx`, `GetModuleHandle` — สำหรับ mouse hook
- `WH_MOUSE_LL = 14`, `WM_MOUSEMOVE = 0x0200`
- struct `MSLLHOOKSTRUCT` (เอา field `pt` ที่เป็น POINT พอ)
- `SendInput`, struct `INPUT`/`KEYBDINPUT` — สำหรับยิง Ctrl+V (virtual key VK_CONTROL=0x11, VK_V=0x56, flag KEYEVENTF_KEYUP=0x0002)
- `GetCursorPos` (ถ้าต้องการตำแหน่งเคอร์เซอร์เพิ่ม)

### 3.3 `MouseShakeDetector.cs`
หัวใจของแอป — ตรวจจับ "การเขย่า":
- ติดตั้ง `WH_MOUSE_LL` hook ตอน start (ต้องเก็บ delegate ไว้เป็น field ระดับ class กันโดน GC เก็บ → ไม่งั้น hook crash)
- เก็บประวัติตำแหน่งเมาส์ `(x, timestamp)` ในหน้าต่างเวลา ~600ms ล่าสุด
- อัลกอริทึมตรวจเขย่า:
  - นับ **จำนวนครั้งที่ทิศทางแกน X กลับด้าน** (reversals) ภายในหน้าต่างเวลา
  - รวมระยะทางการเคลื่อนที่แกน X (total horizontal distance)
  - ถ้า `reversals >= threshold.rev` **และ** `distance >= threshold.dist` → ถือว่าเขย่า
- ตาราง threshold ตามความไว:
  | ระดับ | reversals | distance(px) |
  |---|---|---|
  | Loose (หลวม/ไวมาก) | 3 | 150 |
  | Medium (กลาง) | 4 | 220 |
  | Tight (แน่น/ต้องสะบัดแรง) | 6 | 320 |
- มี cooldown ~400ms หลัง trigger หนึ่งครั้ง กันเด้งซ้ำ
- ยิง event `OnShakeDetected(Point cursorPosition)` ออกไปให้ TrayApplicationContext รับ
- **ข้อควรระวัง:** อย่าทำงานหนักใน hook callback (มันรันบน message loop) — แค่เก็บข้อมูล แล้วตัดสินใจเร็วๆ ถ้าตรวจพบค่อย invoke กลับ UI thread

### 3.4 `FilePicker.cs`
- เมธอด `GetRecentFiles(string folderPath, int maxCount)`:
  - อ่านไฟล์ทั้งหมดในโฟลเดอร์ (เฉพาะไฟล์ ไม่เอาโฟลเดอร์ย่อย)
  - เรียงตาม `LastWriteTime` มาก→น้อย (ใหม่สุดก่อน)
  - คืน path ของ N ไฟล์แรก
- เมธอดช่วยแปลง enum → path จริง: Downloads = `%USERPROFILE%\Downloads`, Desktop = `Environment.GetFolderPath(SpecialFolder.Desktop)`
- กรองไฟล์ชั่วคราว/ไฟล์ระบบออก (เช่น `.crdownload`, `.tmp`, `desktop.ini`) — optional แต่แนะนำ
- **การ map เลขบนวง → จำนวนไฟล์:** วงเป็นแบบโทรศัพท์หมุน เลข `0` ให้หมายถึง **10 ไฟล์** (ไม่ใช่ 0). ดังนั้นจำนวนสูงสุดที่หยิบได้คือ 10. ถ้าโฟลเดอร์มีไฟล์น้อยกว่าที่เลือก ให้หยิบเท่าที่มี และ DialForm ควร disable/หรี่เลขที่เกินจำนวนไฟล์จริง

### 3.5 `DialForm.cs`
วงเลขแบบ **โทรศัพท์หมุน (rotary dial)** บนการ์ดสี่เหลี่ยมมุมมน — สไตล์ Teenage Engineering

**รูปแบบหน้าตา (อ้างอิงจาก demo ที่อนุมัติแล้ว — ดูไฟล์ `yib-demo.html` ประกอบ):**
- `Form` แบบ: `FormBorderStyle=None`, `TopMost=true`, `ShowInTaskbar=false`, `StartPosition=Manual`
- เป็น **การ์ดสี่เหลี่ยมจัตุรัสมุมมนเข้ม** (ไม่ใช่วงกลมลอย) — ขนาดด้านละ ~`min(360px, 86% ของความกว้างจอ, 70% ของความสูงจอ)`
  - มุมมน radius ~19% ของขนาดการ์ด, พื้นหลัง gradient เทาเข้ม (#1d1e22 → #15161a), มีเงา drop shadow ใหญ่ + เส้น border บางๆ
  - ขอบมน + เงา: ใช้ layered window (`SetWindowRgn` ด้วย region มุมมน หรือ `UpdateLayeredWindow`) หรือวาด rounded rect เองด้วย GDI+ แล้วตั้ง `TransparencyKey` ให้พื้นนอกการ์ดโปร่ง
- เปิดขึ้นมาให้ศูนย์กลางการ์ดอยู่ที่ตำแหน่งเคอร์เซอร์ แล้ว **clamp ไม่ให้ล้นขอบจอ** (ใช้ working area ของจอที่เคอร์เซอร์อยู่ — `Screen.FromPoint`)

**องค์ประกอบในการ์ด (วาดด้วย GDI+ ใน `OnPaint`):**
- **วงแหวนเลขแบบโทรศัพท์หมุน:** เลขเรียงรอบวงตามลำดับ **1, 2, 3, 4, 5, 6, 7, 8, 9, 0** (รวม 10 ตำแหน่ง) เริ่มที่ตำแหน่ง 12 นาฬิกาแล้วไล่ตามเข็มนาฬิกา (ห่างกันตำแหน่งละ 36°)
  - แต่ละเลขเป็นปุ่มกลมสีเทา (#34353A) เส้นผ่านศูนย์กลาง ~15.5% ของการ์ด
  - เลขที่ "ถูกเล็ง" อยู่ → เปลี่ยนเป็นสีส้ม (#FF5A1F) ตัวหนังสือเข้ม + มี glow ส้มรอบปุ่ม
  - มี **ขีดบอกตำแหน่ง (tick marks)** สั้นๆ รอบขอบการ์ด คั่นระหว่างเลขแต่ละตัว
- **เส้นเล็ง (aim line):** เส้นสีส้มเรืองแสงลากจากจุดศูนย์กลางการ์ดไปยังเลขที่กำลังเล็งอยู่ (เหมือนเข็มชี้) — โผล่เฉพาะตอนกำลังเลือก
- **จุดศูนย์กลาง (origin dot):** จุดเล็กๆ สีจางตรงกลางการ์ด เป็นจุดตั้งต้นของเส้นเล็ง
- **ตัวเลขใหญ่กลางการ์ด (center readout):**
  - ตัวเลขใหญ่สีส้ม = จำนวนไฟล์ที่กำลังจะหยิบ (font แบบ monospace, ~9% ของการ์ด)
  - บรรทัดล่าง = ข้อความ `"หยิบ N ไฟล์"` (ถ้า N=0 แสดง `"เลื่อนเพื่อเลือกจำนวนไฟล์"`)
  - **แถว thumbnail:** แสดง preview ไฟล์ที่จะหยิบ สูงสุด 3 รูป + ป้าย `+X` ถ้าเกิน (ดู §6 เรื่องวิธีดึง thumbnail — เฟสแรกใช้ไอคอนตามนามสกุลไฟล์แทนได้)
- **ปุ่มสลับโฟลเดอร์ (pills):** ปุ่มเล็ก 2 อันชิดกัน label **"DL"** และ **"DESK"** วางเหนือจุดกึ่งกลางเล็กน้อย — อันที่เลือกอยู่เป็นสีส้ม คลิกเพื่อสลับ Downloads ⇄ Desktop แล้วรีเฟรชจำนวนไฟล์/thumbnail ทันที

**Interaction — โหมด "เลื่อนเล็งแล้วปล่อย" (เหมือน demo):**
- ขณะวงเปิด ติดตามตำแหน่งเมาส์เทียบกับจุดศูนย์กลางการ์ด:
  - คำนวณ **มุม** (`atan2(dy, dx)`) จากจุดศูนย์กลางไปยังเคอร์เซอร์ → หาเลขที่มุมใกล้ที่สุด → ไฮไลต์เลขนั้น + อัปเดตเส้นเล็ง + อัปเดตตัวเลขใหญ่/thumbnail
  - ถ้าเคอร์เซอร์อยู่ **ใกล้จุดศูนย์กลางมากเกินไป** (ระยะ < ~10% ของการ์ด) → ถือว่ายังไม่เลือก (N=0) ไม่ไฮไลต์เลขใด
- **ยืนยัน:** คลิกที่ใดก็ได้ในการ์ด (ยกเว้นปุ่ม DL/DESK) → ปิดวง + เรียก callback `OnNumberPicked(N)` ตามเลขที่เล็งอยู่
- **ยกเลิก:** กด `Esc` หรือคลิกนอกการ์ด (เสีย focus) → ปิดวงไม่ทำอะไร
- เล่นเสียงคลิกตอนเปิดวง และตอนยืนยัน (ถ้า SoundEnabled)

> **หมายเหตุ palette สำหรับ implement:** accent ส้ม `#FF5A1F`, ปุ่มเทา `#34353A`, การ์ด `#1B1C1F`/gradient, ข้อความ `#F5F3EE`, ข้อความจาง `#8C8C90`

> **เฟสแรกทำให้ใช้งานได้ก่อน:** วาดการ์ด + วงเลข 1-0 + เส้นเล็ง + ตัวเลขกลาง + ปุ่ม DL/DESK ก็พอ. ส่วน thumbnail จริงของไฟล์ค่อยเพิ่มทีหลัง — เฟสแรกแสดงเป็นไอคอนตามนามสกุล (สี gradient ต่างกันตามชนิดไฟล์) แทนได้ (ดู §6)

### 3.6 `ClipboardPaster.cs`
- `PasteFiles(IEnumerable<string> filePaths)`:
  - **ลำดับไฟล์:** เรียง **เก่า→ใหม่** ก่อนใส่ clipboard (ให้ตรงพฤติกรรมต้นฉบับ "เรียงเก่า→ใหม่ให้พอดี"). หมายเหตุ: FilePicker คืนมาใหม่→เก่า ดังนั้นต้อง reverse ตรงนี้
  - สร้าง `StringCollection` ใส่ทุก path → `Clipboard.SetFileDropList(collection)`
  - หน่วงสั้นๆ ~80ms ให้ clipboard นิ่ง
  - ส่ง `Ctrl+V` ด้วย `SendInput` (กด Ctrl down → V down → V up → Ctrl up)
- **ข้อควรระวัง clipboard:** `Clipboard` ของ WinForms ต้องเรียกบน STA thread. ถ้าแอปเป็น `[STAThread]` (ซึ่ง WinForms เป็นโดย default) ก็โอเค

### 3.7 `TrayApplicationContext.cs`
- สืบทอด `ApplicationContext`
- สร้าง `NotifyIcon` พร้อมไอคอน + `ContextMenuStrip`:
  ```
  Yib for Windows        (disabled, หัวเมนู)
  ─────────────
  ความไว ▸  หลวม / กลาง / แน่น    (radio check)
  โฟลเดอร์ ▸  Downloads / Desktop  (radio check)
  เสียงคลิก                         (checkbox)
  ─────────────
  เริ่มทำงานตอนเปิดเครื่อง           (checkbox, optional — เขียน registry Run key)
  ออกจากโปรแกรม
  ```
- สร้าง `MouseShakeDetector` + subscribe `OnShakeDetected`:
  - เมื่อเขย่า → เปิด `DialForm` ตรงตำแหน่งเคอร์เซอร์
  - เมื่อ dial คืนเลข N → เรียก `FilePicker.GetRecentFiles` → `ClipboardPaster.PasteFiles`
- โหลด/เซฟ `Settings` เมื่อเปลี่ยนค่าเมนู
- cleanup hook + NotifyIcon ตอน Exit

### 3.8 `Program.cs`
```csharp
[STAThread]
static void Main()
{
    ApplicationConfiguration.Initialize();
    Application.Run(new TrayApplicationContext());
}
```
- เพิ่ม single-instance guard ด้วย `Mutex` (กันเปิดซ้อน 2 ตัว)

---

## 4. `Yib.csproj` (ตั้งค่าสำคัญ)

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net8.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <ApplicationIcon>Resources\yib.ico</ApplicationIcon>
    <AssemblyName>Yib</AssemblyName>
    <!-- single-file build -->
    <PublishSingleFile>true</PublishSingleFile>
    <SelfContained>true</SelfContained>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>
  </PropertyGroup>
</Project>
```

**คำสั่ง build:**
```bash
dotnet publish -c Release -r win-x64
# ได้ไฟล์ที่ bin\Release\net8.0-windows\win-x64\publish\Yib.exe
```

> หมายเหตุ NativeAOT: WinForms รองรับ AOT ได้ตั้งแต่ .NET 8 แต่ยังมีข้อจำกัดบางอย่าง ถ้าจะลองให้เพิ่ม `<PublishAot>true</PublishAot>` แทน PublishSingleFile แล้วทดสอบ. ถ้าเจอ trim warning เยอะ ให้ fallback กลับมา PublishSingleFile ก่อน (ได้ exe ใหญ่กว่าแต่ชัวร์)

---

## 5. ลำดับการ implement (แนะนำให้ Claude Code ทำทีละเฟส)

1. **เฟส 1 — โครงพื้นฐาน:** ตั้งโปรเจกต์ + tray icon ที่มีเมนู "ออกจากโปรแกรม" รันได้ ออกได้
2. **เฟส 2 — mouse hook:** ใส่ `MouseShakeDetector` ทดสอบด้วยการ `Beep` เมื่อตรวจพบเขย่า (ปรับ threshold จนรู้สึกดี)
3. **เฟส 3 — clipboard + paste:** ใส่ `FilePicker` + `ClipboardPaster` ทดสอบ hardcode หยิบ 3 ไฟล์ล่าสุดแล้ว Ctrl+V เข้า Explorer/แชต
4. **เฟส 4 — วงเลข (rotary dial):** ใส่ `DialForm` ตามต้นแบบใน `yib-demo.html` — การ์ดมุมมน + วงเลข 1-0 + เลื่อนเล็งด้วยมุม + เส้นชี้ + ตัวเลขใหญ่กลาง + ปุ่ม DL/DESK แล้วเชื่อมทุกอย่าง (เขย่า→วง→เล็ง→คลิกยืนยัน→แปะ)
5. **เฟส 5 — settings + เมนูเต็ม:** ความไว/โฟลเดอร์/เสียง + เซฟ JSON
6. **เฟส 6 — ขัดเงา:** ไอคอนสวยๆ, ขอบมน, เสียงคลิก, clamp ขอบจอ, single-instance, (optional) auto-start

---

## 6. ฟีเจอร์เสริม (ทำทีหลังได้ ไม่ต้องมีในเวอร์ชันแรก)

- **Preview thumbnail** ของไฟล์ในวงเลข — ดึง thumbnail ด้วย `Windows.Storage` / `IShellItemImageFactory` (ซับซ้อน, ข้ามไปก่อนได้)
- **Multi-monitor aware** — เปิดวงบนจอที่เคอร์เซอร์อยู่จริง (ใช้ `Screen.FromPoint`)
- **กรองชนิดไฟล์** — เลือกหยิบเฉพาะรูป / เฉพาะ pdf ฯลฯ
- **Drag ออกจากวง** แทนการ paste (เผื่อแอปปลายทางไม่รับ Ctrl+V)

---

## 7. ข้อควรระวัง / จุดที่มักพลาด (เตือน Claude Code ไว้)

1. **GC เก็บ hook delegate:** ต้องเก็บ `HookProc` delegate เป็น field ของ class ไม่งั้น hook จะตายเงียบๆ หลัง GC รอบแรก
2. **Clipboard ต้อง STA:** อย่าเรียก `Clipboard.*` จาก background thread
3. **timing ของ Ctrl+V:** ต้องหน่วงหลัง set clipboard เล็กน้อย ไม่งั้นบางแอป paste ไม่ทัน
4. **hook ทำงานหนัก = เมาส์หน่วงทั้งระบบ:** logic ใน callback ต้องเบาและเร็ว
5. **borderless topmost form อาจขโมย focus:** ต้องระวังไม่ให้ตอน paste โฟกัสไปอยู่ที่ตัววงเลข — ปิดวงให้สนิทก่อนยิง Ctrl+V และอาจต้องคืน focus ให้หน้าต่างเดิม (เก็บ `GetForegroundWindow` ก่อนเปิดวง แล้ว `SetForegroundWindow` กลับก่อน paste)
6. **SmartScreen:** exe ที่ไม่ได้เซ็น code-signing จะโดน Windows SmartScreen เตือนตอนเปิดครั้งแรก (เหมือนต้นฉบับที่โดน Gatekeeper บน mac) — เป็นเรื่องปกติ ผู้ใช้กด "More info → Run anyway"

---




