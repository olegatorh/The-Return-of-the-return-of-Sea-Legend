"""Adds Ukrainian transcriptions of character names to lang/ukrainian/strings/_common_extra.tsv (priority over the machine draft).
  names_apply.py <charnames.txt> <_common_extra.tsv>
Only single capitalised words are treated as names; real words and place names stay with the machine draft."""
import re
import sys

sys.path.insert(0, __file__.rsplit('\\', 1)[0] if '\\' in __file__ else '.')
from names_check import translit

WORDS = set('''Надо Отец Блём Бье Второй Кой Матер патер Нунно Эскриба Жардим Уличный Наместник Одноглазый Саргент Красотка Горожанин Флува
Крост Хошуе падре Испанский Английский Французский Загадочный Солдат Корсар Пират Монах Офицер Капитан Торговец Охранник Страж
Стражник Бармен Трактирщик Священник Губернатор Пленник Рыбак Матрос Крестьянин Горожанка Служанка Официантка Хозяин Хозяйка Контрабандист
Купец Ростовщик Судья Палач Нищий Бродяга Проститутка Девушка Старик Старуха Мальчик Ребенок Лекарь Врач Доктор Плотник Кузнец Моряк
Помощник Боцман Штурман Казначей Повар Юнга Часовой Разбойник Бандит Дикарь Индеец Идол Наемник Охотник Скелет'''.split())
FIX = {'Уилльям': 'Вільям', 'Вилльям': 'Вільям', 'Уильям': 'Вільям', 'Вильям': 'Вільям', 'Спэрроу': 'Спарроу', 'Джейкоб': 'Джейкоб',
       'Джакоб': 'Джейкоб', 'Мозес': 'Мозес', 'Израэль': 'Ізраїль', 'Самуэль': 'Самуїл', 'Сэмуель': 'Семюел', 'Мэтью': 'Метью',
       'Эндрю': 'Ендрю', 'Абрахам': 'Авраам', 'Эстебан': 'Естебан', 'Катарина': 'Катаріна', 'Иоланда': 'Іоланда', 'Блэк': 'Блек'}

names = [x for x in open(sys.argv[1], encoding='utf-8').read().split('\n') if x]
path = sys.argv[2]
existing = {}
for line in open(path, encoding='utf-8').read().split('\n'):
    if '\t' in line:
        k, v = line.split('\t', 1)
        existing[k] = v
added = 0
for n in names:
    if n in existing or n in WORDS or not re.fullmatch(r"[А-ЯЁ][а-яё']+", n):
        continue
    existing[n] = FIX.get(n, translit(n))
    added += 1
# the entries given in FIX also win over previous machine guesses of the same words
for k, v in FIX.items():
    if k in names:
        existing[k] = v
with open(path, 'w', encoding='utf-8') as f:
    for k, v in existing.items():
        f.write(f'{k}\t{v}\n')
print('names added:', added, 'total entries:', len(existing))
