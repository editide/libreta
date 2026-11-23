const assert = require('node:assert/strict');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

test('a calculation colours each value it uses once and ignores what those values depend on', () => {
  // Arrange
  const app = loadApp();
  app.set({
    payload: {
      calculations: { 'present_value\u00002026': { tokens: [{ text: 'Jun 30, 2026', dependency: 'valuation_date' }] } }
    }
  });
  const calculation = {
    tokens: [
      { text: 'sum(' },
      { text: '-28,093', dependency: 'present_value', column: '2026', expansionId: 'present_value\u00002026' },
      { text: ', ' },
      { text: '-55,395', dependency: 'present_value', column: '2027' },
      { text: ') + ' },
      { text: '786,769', dependency: 'terminal_value', column: 'value' },
      { text: ' − ' },
      { text: '-28,093', dependency: 'present_value', column: '2026' }
    ]
  };

  // Act
  const references = app.calculationReferences(calculation);

  // Assert
  assert.deepEqual(Object.fromEntries(references), {
    'present_value\u00002026': 0,
    'present_value\u00002027': 1,
    'terminal_value\u0000value': 2
  });
});
