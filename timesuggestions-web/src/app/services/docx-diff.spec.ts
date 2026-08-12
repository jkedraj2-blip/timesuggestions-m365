import { describe, expect, it } from 'vitest';
import { strToU8, zipSync } from 'fflate';
import {
  CURRENT_VERSION_LABEL,
  diffLines,
  extractLines,
  isDiffableDocument,
  versionContentUrl,
} from './docx-diff';

/**
 * Minimalny .docx spreparowany w teście: ZIP z samym word/document.xml
 * w przestrzeni nazw WordprocessingML — dokładnie to, co czyta parser.
 * Akapit może mieć wiele runów (w:r); run '\n' zapisujemy jako w:br,
 * a '\t' jako w:tab, czyli tak, jak Word zapisuje miękki enter i tabulator.
 */
function createMinimalDocx(paragraphs: string[][]): Uint8Array {
  const runXml = (run: string): string => {
    if (run === '\n') {
      return '<w:r><w:br/></w:r>';
    }
    if (run === '\t') {
      return '<w:r><w:tab/></w:r>';
    }
    return `<w:r><w:t>${run}</w:t></w:r>`;
  };
  const body = paragraphs
    .map((runs) => `<w:p>${runs.map(runXml).join('')}</w:p>`)
    .join('');
  const documentXml =
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">' +
    `<w:body>${body}</w:body></w:document>`;
  return zipSync({ 'word/document.xml': strToU8(documentXml) });
}

describe('extractLines', () => {
  it('wyciąga tekst akapitów, łącząc w:t z wielu runów', () => {
    const docx = createMinimalDocx([
      ['Pierwszy akapit'],
      ['Drugi ', 'akapit ', 'z trzech runów'],
    ]);

    expect(extractLines(docx)).toEqual(['Pierwszy akapit', 'Drugi akapit z trzech runów']);
  });

  /**
   * Sedno czytelności listy zmian: dla użytkownika enter i Shift+Enter to ta sama rzecz.
   * Przy jednostce „akapit" kilkanaście widocznych linii lądowało w jednej pozycji diffu
   * — z jednym przyciskiem „Pokaż całość" i licznikiem znaków dla całego bloku.
   */
  it('miękki enter (w:br) zaczyna NOWĄ linię, a nie skleja wyrazów w jeden ciąg', () => {
    const docx = createMinimalDocx([['Koniec umowy', '\n', 'Strony ustalają']]);

    expect(extractLines(docx)).toEqual(['Koniec umowy', 'Strony ustalają']);
  });

  it('tabulator (w:tab) zostaje tabulatorem — wcięcia list nie znikają', () => {
    const docx = createMinimalDocx([['\t', 'punkt pierwszy']]);

    expect(extractLines(docx)).toEqual(['\tpunkt pierwszy']);
  });

  it('pusta linia zostaje pustym stringiem — i to niezależnie od rodzaju entera', () => {
    expect(extractLines(createMinimalDocx([['Przed'], [], ['Po']])))
      .toEqual(['Przed', '', 'Po']);
    // Ten sam układ zrobiony miękkimi enterami wewnątrz jednego akapitu.
    expect(extractLines(createMinimalDocx([['Przed', '\n', '\n', 'Po']])))
      .toEqual(['Przed', '', 'Po']);
  });

  it('archiwum bez word/document.xml to czytelny błąd, nie wyjątek fflate', () => {
    const notDocx = zipSync({ 'inny/plik.txt': strToU8('zawartość') });

    expect(() => extractLines(notDocx)).toThrow('word/document.xml');
  });
});

describe('diffLines', () => {
  it('rozpoznaje linię dodaną i usuniętą', () => {
    const changes = diffLines(['A', 'B'], ['A', 'B', 'C']);
    expect(changes).toEqual([{ kind: 'added', text: 'C' }]);

    const removals = diffLines(['A', 'B', 'C'], ['A', 'C']);
    expect(removals).toEqual([{ kind: 'removed', text: 'B' }]);
  });

  it('paruje usunięcie z dodaniem w tym samym miejscu jako zmianę', () => {
    const changes = diffLines(['A', 'stare brzmienie', 'C'], ['A', 'nowe brzmienie', 'C']);

    expect(changes).toEqual([
      { kind: 'changed', text: 'nowe brzmienie', previousText: 'stare brzmienie' },
    ]);
  });

  it('nadwyżka w grupie zmian zostaje czystym dodaniem', () => {
    const changes = diffLines(['A', 'X'], ['A', 'Y', 'Z']);

    expect(changes).toEqual([
      { kind: 'changed', text: 'Y', previousText: 'X' },
      { kind: 'added', text: 'Z' },
    ]);
  });

  it('identyczny tekst = pusta lista (UI mówi o zmianach formatowania)', () => {
    expect(diffLines(['A', 'B'], ['A', 'B'])).toEqual([]);
  });

  /**
   * Blok wielolinijkowy rozbity na linie daje osobną pozycję na każdą z nich —
   * każda z własnym licznikiem znaków i własnym przyciskiem rozwinięcia. Wcześniej
   * cały taki blok był jedną pozycją i jednym przyciskiem gdzieś na górze.
   */
  it('zmieniona jest tylko ta linia, która się zmieniła — nie cały blok', () => {
    const before = ['Nagłówek', 'Pierwsza linia', 'Druga linia', 'Stopka'];
    const after = ['Nagłówek', 'Pierwsza linia', 'Druga linia poprawiona', 'Stopka'];

    expect(diffLines(before, after)).toEqual([
      { kind: 'changed', text: 'Druga linia poprawiona', previousText: 'Druga linia' },
    ]);
  });

  it('seria enterów to jedna pozycja z licznikiem, nie tyle wierszy, ile enterów', () => {
    const changes = diffLines(['A'], ['A', '', '', '', 'B']);

    expect(changes).toEqual([
      { kind: 'added', text: '', emptyLines: 3 },
      { kind: 'added', text: 'B' },
    ]);
  });

  it('sklejanie nie przechodzi przez linię z tekstem ani przez zmianę rodzaju', () => {
    // Dwa odstępy rozdzielone linią z treścią to dwa osobne odstępy.
    expect(diffLines(['A'], ['A', '', 'tekst', '', ''])).toEqual([
      { kind: 'added', text: '', emptyLines: 1 },
      { kind: 'added', text: 'tekst' },
      { kind: 'added', text: '', emptyLines: 2 },
    ]);

    // Usunięty odstęp to nie to samo co dodany — liczniki nie mogą się zlać.
    expect(diffLines(['A', '', ''], ['A', 'tekst'])).toEqual([
      { kind: 'changed', text: 'tekst', previousText: '' },
      { kind: 'removed', text: '', emptyLines: 1 },
    ]);
  });

  it('pozycja "zmienione" nie jest sklejana, nawet gdy jedna strona jest pusta', () => {
    const changes = diffLines(['stare', 'drugie'], ['', '']);

    expect(changes).toEqual([
      { kind: 'changed', text: '', previousText: 'stare' },
      { kind: 'changed', text: '', previousText: 'drugie' },
    ]);
  });
});

describe('versionContentUrl', () => {
  it('bieżąca wersja pobierana z endpointu pliku — jej treści Graph nie wydaje spod adresu wersji (400)', () => {
    // Numer wersji jest zwykły (9.0) — o wyjątku decyduje to, że to najnowszy zapis.
    const url = versionContentUrl('01ABC', { versionId: '9.0', isCurrent: true });

    expect(url).toBe('https://graph.microsoft.com/v1.0/me/drive/items/01ABC/content');
    expect(url).not.toContain('/versions/');
  });

  it('etykieta "current" zamiast numeru też prowadzi do endpointu pliku', () => {
    const url = versionContentUrl('01ABC', { versionId: CURRENT_VERSION_LABEL, isCurrent: false });

    expect(url).toBe('https://graph.microsoft.com/v1.0/me/drive/items/01ABC/content');
  });

  it('wersja historyczna pobierana z endpointu wersji', () => {
    expect(versionContentUrl('01ABC', { versionId: '3.0', isCurrent: false })).toBe(
      'https://graph.microsoft.com/v1.0/me/drive/items/01ABC/versions/3.0/content',
    );
  });

  it('identyfikatory trafiają do adresu zakodowane', () => {
    expect(versionContentUrl('a/b', { versionId: 'v 1', isCurrent: false }))
      .toContain('items/a%2Fb/versions/v%201/content');
  });
});

describe('isDiffableDocument', () => {
  it('diff obejmuje wyłącznie .docx — .doc i Excel dostają chronologię bez diffu', () => {
    expect(isDiffableDocument('Umowa.docx')).toBe(true);
    expect(isDiffableDocument('UMOWA.DOCX')).toBe(true);
    expect(isDiffableDocument('Umowa.doc')).toBe(false);
    expect(isDiffableDocument('Arkusz.xlsx')).toBe(false);
    expect(isDiffableDocument(null)).toBe(false);
  });
});
