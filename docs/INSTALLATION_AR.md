# تثبيت وتشغيل GhostHunterPC على Windows

## النسخة المنشورة

نسخة Windows x64 المستقلة المنشورة موجودة في:

`artifacts\publish\win-x64\GhostHunterPC.exe`

هذه نسخة `Release` مستقلة (self-contained) تستهدف `win-x64` و`net10.0-windows`. لا تتطلب تثبيت .NET Desktop Runtime على الجهاز، لكنها مخصصة لنظام Windows ذي معمارية x64. شغّل `GhostHunterPC.exe` مباشرة. قد يعرض Windows تحذير SmartScreen لأن التطبيق غير موقّع رقميًا؛ لا تتجاوز التحذير إلا إذا كنت تثق بمصدر النسخة.

يجب إبقاء مجلد `Resources` الموجود بجانب الملف التنفيذي مع النسخة؛ فهو يحتوي موارد الترجمة العربية والإنجليزية. حجم الملف التنفيذي المنشور نحو 133.6 MiB، والحجم الإجمالي لمخرجات النشر نحو 133.7 MiB.

لم يُنشأ Setup.exe أو MSI أو MSIX. النشر المستقل ملف تشغيل، وليس برنامج تثبيت.

## البيانات المحلية والخصوصية

يحفظ التطبيق الإعدادات والسجل المحلي تحت:

`%LOCALAPPDATA%\GhostHunterPC\`

يُحفظ تقرير التصدير في الموقع الذي تختاره عند إنشاء التقرير. لا ترفع النسخة التطبيقية أو التقارير إلى GitHub تلقائيًا؛ راجع الملفات قبل مشاركة أي منها، فقد تتضمن بعض بيانات السجل أسماء برامج أو عمليات ومسارات بدء التشغيل.

## البناء من المصدر

المتطلبات:

- Windows x64.
- .NET 10 SDK.
- Git لاستنساخ المستودع (اختياري إذا كان المصدر موجودًا محليًا).

من PowerShell في مجلد المشروع:

```powershell
dotnet --version
dotnet restore .\GhostHunterPC.sln
dotnet build .\GhostHunterPC.sln --configuration Release
```

لتشغيل نسخة التطوير:

```powershell
dotnet run --project .\src\GhostHunterPC\GhostHunterPC.csproj
```

لنشر نسخة مستقلة جديدة لـWindows x64:

```powershell
dotnet publish .\src\GhostHunterPC\GhostHunterPC.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  --output .\artifacts\publish\win-x64
```

## إعداد مستودع GitHub يدويًا

1. أنشئ مستودعًا فارغًا في GitHub، وحدد مالكه واسمه ورؤيته العامة أو الخاصة.
2. من جذر نسخة المصدر، تحقق من الملفات التي ستدخل في المستودع باستخدام `git status`، ثم أضف فقط ملفات المصدر والوثائق المناسبة.
3. اربط المستودع المحلي بعنوان HTTPS أو SSH الذي أنشأته، ثم ادفع الفرع الرئيسي وفق سياسة فريقك. مثال بعد استبدال العنوان بعنوان مستودعك:

```powershell
git remote add origin <repository-url>
git branch -M main
git push -u origin main
```

لا تضع كلمات مرور أو مفاتيح API أو مفاتيح توقيع أو رموز وصول في ملفات المشروع أو سجل Git. استخدم إعدادات GitHub Secrets للبيانات السرية اللازمة لأتمتة موثوقة، ودوّر أي سر تم نشره بالخطأ. لا ترفع تقارير المستخدمين أو ملفات `settings.json` أو `pc-history.json` أو بيانات محلية خاصة.

لم يتم إنشاء مستودع GitHub أو رفع أي ملف في هذه المهمة.
