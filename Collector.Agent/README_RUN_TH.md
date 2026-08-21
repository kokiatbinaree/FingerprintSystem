# ทดลอง Collector.Agent

1. ติดตั้ง Futronic Driver/Bridge ให้เรียบร้อย
2. ตรวจว่า bridge เดิมตอบที่ `http://127.0.0.1:15270/fpoperation`
3. เปิด Command Prompt ในโฟลเดอร์ `Collector.Agent`
4. รัน `dotnet build`
5. รัน `run.bat`
6. Agent จะฟังที่ `http://127.0.0.1:15271`

ทดสอบ:

- `GET http://127.0.0.1:15271/health`
- `GET http://127.0.0.1:15271/scanner/status`
- `POST http://127.0.0.1:15271/scanner/capture` ด้วย body `{ "timeoutSeconds": 30 }`

ถ้า capture สำเร็จ response จะมี `pngBase64`, `width`, `height`
