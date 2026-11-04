"""Hand corrections of the machine-translated item names/headers (lang/ukrainian/texts/ItemsDescribe.txt).
Run again after re-generating the draft: it only overwrites the listed keys.  Usage: polish_items.py <ItemsDescribe.txt>"""
import re
import sys

FIX = {
    'chest': 'Скриня', 'piece_of_legend': 'Скрижаль', 'notes_of_legend': 'Запис легенди',
    'letter_1': 'Запечатаний лист', 'letter_2': 'Запечатаний лист', 'letter_open': 'Розпечатаний лист',
    'love_letters_of_priest': 'Любовні листи священика', 'letter_to_domingues': 'Лист для падре Домінгеса',
    'letter_to_bernard': 'Лист для отця Бернарда', 'prison_letter': 'Лист від отця Бернарда',
    'massoni_papers': 'Папери Валентина Массоні', 'story_gramota': 'Документи', 'map_isla': 'Карта Ісла-Муерти',
    'map_sm': 'Карта Сен-Мартена', 'map_puerto': 'Карта Пуерто-Рико', 'necronomicon': 'Некрономікон',
    'Powder_mummie': 'Порошок мумії', 'Rock_letter': 'Скрижалі', 'unarmed': '"Беззбройний"', 'blade1': '"Стара шабля"',
    'blade4': '"Абордажна шабля"', 'blade8': '"Мальтійський меч"', 'blade9': '"Італійська рапіра"', 'blade13': '"Есток"',
    'blade19': '"Німецька рапіра"', 'blade20': '"Атилл"', 'blade25': '"Сирійська шабля"', 'blade27': '"Рапіра Моргана"',
    'blade34': '"Німча"', 'arm1': '"Бойові пазурі"', 'pistol1': 'Пістоль', 'pistol2': 'Довгоствольний пістоль',
    'pistol4': 'Чотириствольний пістоль', 'pistol5': 'Бретерський пістоль', 'pistol6': 'Двоствольний пістоль',
    'cirass1': 'Звичайна кіраса', 'cirass3': 'Іспанська кіраса', 'cirass5': 'Дорога кіраса', 'nocirass': 'Без кіраси',
    'spyglass1': 'Дешева підзорна труба', 'spyglass2': 'Звичайна підзорна труба', 'spyglass3': 'Гарна підзорна труба',
    'spyglass4': 'Чудова підзорна труба', 'spyglass5': 'Ексклюзивна підзорна труба', 'potion1': 'Лікувальне зілля',
    'potion3': 'Протиотрута', 'jewelry4': 'Смарагд', 'jewelry6': 'Срібний перстень із сапфіром',
    'jewelry7': 'Золотий перстень зі смарагдом', 'jewelry8': 'Бронзовий перстень', 'jewelry10': 'Золотий перстень із сапфіром',
    'jewelry11': 'Велика перлина', 'jewelry12': 'Маленька перлина', 'jewelry14': 'Золота брошка',
    'jewelry15': 'Смарагдові підвіски', 'jewelry16': 'Намисто', 'jewelry17': 'Срібний злиток',
    'jewelry18': 'Золотий перстень із рубіном', 'indian2': 'Статуетка дивної тварини', 'indian3': 'Маленька мозаїка',
    'indian4': 'Глиняний глечик', 'indian6': 'Статуетка Акумаларту', 'indian8': 'Королівська плювальниця',
    'indian11': 'Щурячий бог', 'indian13': 'Давня курильниця', 'indian16': 'Розфарбований горщик',
    'indian17': 'Статуетка тигра', 'indian21': 'Церемоніальна посудина', 'potionrum': 'Пляшка рому',
    '100gp': '100 піастрів', '250gp': '250 піастрів', '500gp': '500 піастрів', 'Mineral5': 'Гірський кришталь',
    'Mineral6': 'Корал', 'Mineral10': 'Кам\'яна сіль', 'suit_1': 'Лахміття', 'potion5': 'Виноград',
}
HEAD = {
    'Potion parameters': 'Ємність',
    'weapon blade parameters': 'Зброя: шкода мін/макс #ddmg_min#/#ddmg_max#, пробиття/блок #dpiercing#/#dblock#.',
    'weapon blade type': 'Тип: #sFencingTypeName#.',
    'weapon gun parameters': 'Зброя: шкода мін/макс #ddmg_min#/#ddmg_max#, час перезарядки #dchargespeed# с, точність #daccuracy#%.',
    'youve_got': 'Додано предмет',
}

path = sys.argv[1]
text = open(path, encoding='utf-8').read()
n = 0
for key, val in FIX.items():
    text, c = re.subn(r'^(itmname_' + re.escape(key) + r'\s*)\{[^}]*\}', lambda m: m.group(1) + '{' + val + '}', text, flags=re.M)
    n += c
for key, val in HEAD.items():
    text, c = re.subn(r'^(' + re.escape(key) + r'\s*)\{[^}]*\}', lambda m: m.group(1) + '{' + val + '}', text, flags=re.M)
    n += c
open(path, 'w', encoding='utf-8').write(text)
print('fixed', n, 'entries')
