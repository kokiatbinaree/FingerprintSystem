# Collector.Agent

Local Windows agent สำหรับเชื่อม Collector Web/API กับ Futronic FS80H ผ่าน local bridge/SDK

## เป้าหมาย

- ตรวจสถานะ scanner
- เริ่ม capture
- รอผล capture
- ดึงภาพ grayscale จริง
- ส่งผลกลับให้ Local API

Agent จะฟังเฉพาะ localhost และไม่เปิด scanner ให้เครื่องอื่นใน network โดยตรง

> ขั้นแรกใช้ Futronic Bridge ที่ติดตั้งอยู่แล้วบนเครื่อง และ endpoint `http://127.0.0.1:15270/fpoperation`
