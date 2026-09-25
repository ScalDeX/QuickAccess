namespace QuickAccess;

public static class IconGlyphs
{
    public static readonly List<(int Code, string Label)> All = new()
    {
        (0xE8B7, "Папка"),
        (0xE80F, "Дом"),
        (0xE721, "Поиск"),
        (0xE713, "Настройки"),
        (0xE7FC, "Игры"),
        (0xE774, "Интернет"),
        (0xE8D6, "Музыка"),
        (0xE714, "Видео"),
        (0xE722, "Камера"),
        (0xE715, "Почта"),
        (0xE943, "Код"),
        (0xE756, "Терминал"),
        (0xE896, "Загрузка"),
        (0xE898, "Выгрузка"),
        (0xE719, "Магазин"),
        (0xE734, "Звезда"),
        (0xE813, "Сердце"),
        (0xE753, "Облако"),
        (0xE7B8, "Документ"),
        (0xE787, "Календарь"),
        (0xE77B, "Контакт"),
        (0xE717, "Телефон"),
        (0xE8F2, "Чат"),
        (0xE72E, "Замок"),
        (0xE90F, "Инструмент"),
        (0xE74D, "Корзина"),
    };

    public static string ToGlyph(int code) => char.ConvertFromUtf32(code);
}
