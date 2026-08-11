import { describe, expect, it } from 'vitest';
import { strToU8, zipSync } from 'fflate';
import { diffParagraphs, extractParagraphs, isDiffableDocument } from './docx-diff';

/**
 * Minimalny .docx spreparowany w teście: ZIP z samym word/document.xml
 * w przestrzeni nazw WordprocessingML — dokładnie to, co czyta parser.
 * Akapit może mieć wiele runów (w:r) — tekst to złączone w:t per w:p.
 */
function createMinimalDocx(paragraphs: string[][]): Uint8Array {
  const body = paragraphs
    .map((runs) => `<w:p>${runs.map((run) => `<w:r><w:t>${run}</w:t></w:r>`).join('')}</w:p>`)
    .join('');
  const documentXml =
    '<?xml version="1.0" encoding="UTF-8" standalone="yes"?>' +
    '<w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main">' +
    `<w:body>${body}</w:body></w:document>`;
  return zipSync({ 'word/document.xml': strToU8(documentXml) });
}

describe('extractParagraphs', () => {
  it('wyciąga tekst akapitów, łącząc w:t z wielu runów', () => {
    const docx = createMinimalDocx([
      ['Pierwszy akapit'],
      ['Drugi ', 'akapit ', 'z trzech runów'],
    ]);

    expect(extractParagraphs(docx)).toEqual(['Pierwszy akapit', 'Drugi akapit z trzech runów']);
  });

  it('pusty akapit zostaje pustym stringiem (nie przesuwa diffu)', () => {
    const docx = createMinimalDocx([['Przed'], [], ['Po']]);

    expect(extractParagraphs(docx)).toEqual(['Przed', '', 'Po']);
  });

  it('archiwum bez word/document.xml to czytelny błąd, nie wyjątek fflate', () => {
    const notDocx = zipSync({ 'inny/plik.txt': strToU8('zawartość') });

    expect(() => extractParagraphs(notDocx)).toThrow('word/document.xml');
  });
});

describe('diffParagraphs', () => {
  it('rozpoznaje akapit dodany i usunięty', () => {
    const changes = diffParagraphs(['A', 'B'], ['A', 'B', 'C']);
    expect(changes).toEqual([{ kind: 'added', text: 'C' }]);

    const removals = diffParagraphs(['A', 'B', 'C'], ['A', 'C']);
    expect(removals).toEqual([{ kind: 'removed', text: 'B' }]);
  });

  it('paruje usunięcie z dodaniem w tym samym miejscu jako zmianę', () => {
    const changes = diffParagraphs(['A', 'stare brzmienie', 'C'], ['A', 'nowe brzmienie', 'C']);

    expect(changes).toEqual([
      { kind: 'changed', text: 'nowe brzmienie', previousText: 'stare brzmienie' },
    ]);
  });

  it('nadwyżka w grupie zmian zostaje czystym dodaniem', () => {
    const changes = diffParagraphs(['A', 'X'], ['A', 'Y', 'Z']);

    expect(changes).toEqual([
      { kind: 'changed', text: 'Y', previousText: 'X' },
      { kind: 'added', text: 'Z' },
    ]);
  });

  it('identyczny tekst = pusta lista (UI mówi o zmianach formatowania)', () => {
    expect(diffParagraphs(['A', 'B'], ['A', 'B'])).toEqual([]);
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
