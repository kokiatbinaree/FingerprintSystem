# FingerprintSystem

ระบบเก็บและจัดการลายนิ้วมือแบบ Offline-first สำหรับ Futronic FS80H

## โครงสร้าง

- `OfflineFingerprint.Collector/` — ASP.NET Core Local API, SQLite, encrypted local fingerprint storage และ Futronic Bridge adapter
- `OfflineFingerprint.Collector.Web/` — React + TypeScript UI สำหรับ Collector

## เป้าหมาย

1. Login
2. เพิ่ม/ค้นหา Person
3. เก็บลายนิ้วมือ 10 นิ้วผ่าน Futronic FS80H
4. เก็บข้อมูลและภาพแบบ Offline ก่อน
5. ผูกภาพกับ Person, FingerCode, Position และ Sequence
6. เตรียม Sync Online ภายหลัง

## เริ่มต้น

### API

```bat
cd OfflineFingerprint.Collector
setup.bat
run.bat
```

API: `http://localhost:5140`

บัญชีทดสอบเริ่มต้น:

- Username: `admin`
- Password: `ChangeMe123!`

### Web

```bat
cd OfflineFingerprint.Collector.Web
setup.bat
run.bat
```

Web: `http://127.0.0.1:5173`

> สำหรับ Windows ที่ใช้ Futronic Bridge ให้ติดตั้ง Bridge/driver ที่เครื่องก่อนเริ่ม Capture จริง
