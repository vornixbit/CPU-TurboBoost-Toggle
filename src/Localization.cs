using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace TurboToggle;

static class Localization
{
    public static readonly string[] AllLangs = { "en", "ru", "uk", "zh" };
    static readonly FrozenDictionary<string, string> LangNames = new Dictionary<string, string>
    {
        ["en"] = "English",
        ["ru"] = "Русский",
        ["uk"] = "Українська",
        ["zh"] = "简体中文",
    }.ToFrozenDictionary();

    static int _currentLangIdx;

    public static string Language
    {
        get => AllLangs[_currentLangIdx];
        set
        {
            _currentLangIdx = Array.IndexOf(AllLangs, value);
            if (_currentLangIdx < 0)
                _currentLangIdx = 0;
        }
    }

    [DllImport("kernel32.dll")]
    static extern ushort GetUserDefaultUILanguage();

    static readonly FrozenDictionary<string, string[]> Strings = new Dictionary<string, string[]>
    {
        ["status_on"]    = new[] { "Turbo Boost: On",      "Turbo Boost: Включен",      "Turbo Boost: Увімкнено", "Turbo Boost: 已启用" },
        ["status_off"]   = new[] { "Turbo Boost: Off",     "Turbo Boost: Выключен",     "Turbo Boost: Вимкнено", "Turbo Boost: 已禁用" },
        ["enable"]       = new[] { "Enable",               "Включить",                  "Увімкнути", "启用" },
        ["disable"]      = new[] { "Disable",              "Выключить",                 "Вимкнути", "禁用" },
        ["exit"]         = new[] { "Exit",                 "Выход",                     "Вихід", "退出" },
        ["settings"]     = new[] { "Settings",             "Настройки",                 "Налаштування", "设置" },
        ["restore_on_exit"] = new[] { "Enable Turbo Boost on exit", "Включать Turbo Boost при выходе", "Вмикати Turbo Boost під час виходу", "退出时启用 Turbo Boost" },
        ["notifications"] = new[] { "Push notifications on mode change", "Пуш-уведомления при смене режима", "Пуш-сповіщення при зміні режиму", "模式切换时推送通知" },
        ["check_updates"] = new[] { "Check for updates", "Проверить обновления", "Перевірити оновлення", "检查更新" },
        ["auto_update_check"] = new[] { "Check for updates automatically", "Проверять обновления автоматически", "Перевіряти оновлення автоматично", "自动检查更新" },
        ["update_available"] = new[] { "Update available:", "Доступно обновление:", "Доступне оновлення:", "有可用更新：" },
        ["up_to_date"] = new[] { "You have the latest version.", "У вас последняя версия.", "У вас остання версія.", "已是最新版本。" },
        ["no_releases"] = new[] { "No releases published yet.", "Релизы ещё не опубликованы.", "Релізи ще не опубліковані.", "还没有发布版本。" },
        ["update_check_failed"] = new[] { "Update check failed (no connection?).", "Не удалось проверить обновления (нет сети?).", "Не вдалося перевірити оновлення (немає мережі?).", "检查更新失败（无网络连接？）" },
        ["open_releases_page"] = new[] { "Open the releases page?", "Открыть страницу релизов?", "Відкрити сторінку релізів?", "打开发布页面？" },
        ["language"]     = new[] { "Language",             "Язык",                      "Мова", "语言" },
        ["hotkey"]       = new[] { "Hotkey",               "Горячая клавиша",           "Гаряча клавіша", "快捷键" },
        ["custom"]       = new[] { "Custom...",            "Своя...",                   "Власна...", "自定义..." },
        ["press_keys"]   = new[] { "Press the new hotkey combination...", "Нажмите новое сочетание клавиш...", "Натисніть нову комбінацію клавіш...", "请按下新的快捷键组合..." },
        ["cancel_hint"]  = new[] { "Esc to cancel",        "Esc — отмена",              "Esc — скасувати", "按 Esc 取消" },
        ["hotkey_set"]   = new[] { "Hotkey set:",          "Сочетание задано:",         "Комбінацію встановлено:", "快捷键已设置：" },
        ["hotkey_failed"]= new[] { "Failed to register hotkey (combination busy). Use the tray menu instead.",
                                   "Не удалось зарегистрировать горячую клавишу (сочетание занято). Управление через меню трея.",
                                   "Не вдалося зареєструвати гарячу клавішу (комбінація зайнята). Керуйте через меню трею.",
                                   "注册快捷键失败（组合键被占用）。请改用托盘菜单。" },
        ["hotkey_busy"]  = new[] { "Combination is already in use by another program.",
                                   "Сочетание уже занято другой программой.",
                                   "Комбінація вже зайнята іншою програмою.",
                                   "该组合键已被其他程序占用。" },
        ["cannot_scheme"]= new[] { "Failed to get the active power scheme.", "Не удалось получить активную схему питания.", "Не вдалося отримати активну схему живлення.", "无法获取当前电源方案。" },
        ["cannot_confirm"]=new[] { "Failed to confirm the state.", "Не удалось подтвердить состояние.", "Не вдалося підтвердити стан.", "无法确认状态。" },
        ["not_applied"]  = new[] { "Value was not applied (firmware/OEM or XTU overriding).",
                                   "Значение не применилось (прошивка/OEM или XTU переопределяет).",
                                   "Значення не застосувалося (прошивка/OEM або XTU перевизначає).",
                                   "设置未生效（固件/OEM 或 XTU 覆盖）。" },
        ["turbo_enabled"]= new[] { "Turbo Boost: enabled",  "Turbo Boost: включен",      "Turbo Boost: увімкнено", "Turbo Boost: 已启用" },
        ["turbo_disabled"]=new[] { "Turbo Boost: disabled", "Turbo Boost: выключен",     "Turbo Boost: вимкнено", "Turbo Boost: 已禁用" },
        ["already_running"] = new[] { "Turbo Toggle is already running.", "Turbo Toggle уже запущен.", "Turbo Toggle вже запущено.", "Turbo Toggle 已在运行。" },
        ["autostart"]    = new[] { "Run at startup",       "Автозапуск",                "Автозапуск", "开机自启动" },
        ["autostart_on"] = new[] { "Autostart enabled",    "Автозапуск включен",        "Автозапуск увімкнено", "开机自启动已启用" },
        ["autostart_off"]= new[] { "Autostart disabled",   "Автозапуск выключен",       "Автозапуск вимкнено", "开机自启动已禁用" },
        ["autostart_error"] = new[] { "Failed to change autostart", "Не удалось изменить автозапуск", "Не вдалося змінити автозапуск", "更改自启动设置失败" },
        ["capture_none"] = new[] { "No hotkey captured.",  "Сочетание не захвачено.",   "Комбінацію не захоплено.", "未捕获到快捷键。" },
        ["power_mode_override"] = new[] { "Power mode overrides the boost setting.",
                                           "Режим электропитания перекрывает настройку boost.",
                                           "Режим живлення перевизначає налаштування boost.",
                                           "电源模式覆盖了 Turbo Boost 设置。" },
        ["github_donate"] = new[] { "GitHub & Donate", "GitHub и донат", "GitHub та донат", "GitHub 与捐赠" },
        ["disabled"] = new[] { "Disabled", "Отключена", "Вимкнена", "已禁用" },
        ["hotkey_unsupported"] = new[] { "— not supported", "— не поддерживается", "— не підтримується", "— 不支持" },
        ["hotkey_disabled"] = new[] { "Hotkey disabled.", "Горячая клавиша отключена.", "Гарячу клавішу вимкнено.", "快捷键已禁用。" },
        ["admin_required"] = new[] { "Administrator rights are required to change power settings.", "Для изменения параметров питания требуются права администратора.", "Для зміни параметрів живлення потрібні права адміністратора.", "更改电源设置需要管理员权限。" },
    }.ToFrozenDictionary();

    public static bool IsSupported(string code) => Array.IndexOf(AllLangs, code) >= 0;

    public static string LangName(string code) =>
        LangNames.TryGetValue(code, out var name) ? name : code;

    public static string Tr(string key)
    {
        int idx = _currentLangIdx;
        return Strings.TryGetValue(key, out var arr) && arr.Length > 0
            ? arr[idx < arr.Length ? idx : 0]
            : key;
    }

    public static string Detect()
    {
        try
        {
            return (GetUserDefaultUILanguage() & 0x3FF) switch
            {
                0x19 => "ru",
                0x22 => "uk",
                0x04 => "zh",
                _ => "en",
            };
        }
        catch
        {
            return "en";
        }
    }
}
