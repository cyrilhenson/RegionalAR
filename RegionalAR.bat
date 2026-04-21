@echo off
cd /d "%~dp0"
python dicom_processor.py
if errorlevel 1 pause
