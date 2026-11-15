# LAN Share (.NET MAUI, Windows)

Застосунок на основі `lan_share_setup.bat`:

- **Налаштувати комп'ютер** - одна кнопка виконує пункти 1 і 8 з bat-файлу
  (мережа, служби, SMB, брандмауер, права мережевого входу) та вмикає віддалений робочий стіл (RDP). Запитує UAC лише для цього кроку.
- **Мій комп'ютер → Спільні папки** - додавання (з вибором папки, імені та рівня доступу) і скасування спільного доступу, як у пунктах 2-4 bat-файлу.
- **Комп'ютери в мережі** - сканування підмережі /24 (порт 445), без `net view`.
- **Клік на комп'ютер** - нове вікно зі списком його спільних папок; клік на папку відкриває її у Провіднику.
  Якщо потрібен вхід, у вікні з'являється форма логіна й пароля.

## Вимоги

- Windows 10 (1809+) або Windows 11
- .NET 10 SDK
- Workload MAUI: `dotnet workload install maui`
  (або Visual Studio із компонентом ".NET Multi-platform App UI development")

## Запуск

```
dotnet run -f net10.0-windows10.0.19041.0
```

## Публікація в папку (портативна версія)

```
dotnet publish -c Release -f net10.0-windows10.0.19041.0 -p:WindowsPackageType=None -p:WindowsAppSDKSelfContained=true
```

Результат лежить у `bin\Release\net10.0-windows10.0.19041.0\win-x64\publish`.
Скопіюйте папку на кожен комп'ютер і запустіть `LanShare.exe`.

## Структура

```
LanShare.csproj           проєкт (лише Windows, unpackaged)
App.xaml(.cs)             стилі та вікно застосунку
MainPage.xaml(.cs)        налаштування + список комп'ютерів
SharesPage.xaml(.cs)      вікно зі спільними папками комп'ютера
Services/NetworkScanner   пошук комп'ютерів (TCP 445)
Services/ShareBrowser     NetShareEnum + вхід через WNetAddConnection2
Services/ElevatedScript    запуск вбудованих .ps1 від імені адміністратора
Services/SetupService     налаштування комп'ютера (setup.ps1)
Services/SharingService   додавання/видалення спільних папок (share.ps1)
Services/FolderPickerService  діалог вибору папки
Scripts/setup.ps1         налаштування (вбудований ресурс)
Platforms/Windows/        WinUI-обгортка
```

## Журнали

Усі технічні журнали (кроки налаштування, діагностика підключень, RDP) пишуться лише
через `Debug.WriteLine()` і видимі у вікні Output/Debug у Visual Studio (збірка Debug).
У самому застосунку журналів немає.
