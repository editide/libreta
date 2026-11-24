const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const { loadApp } = require('./helpers/appHarness');

const app = loadApp();
const css = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.css'), 'utf8');

test('the source preview crops around the selected source region', () => {
  const region = { x: 650, y: 341, width: 71, height: 22 };

  assert.equal(app.sourceImageViewBox(816, 720, region), '390 245.5 426 213');
});

test('the source preview keeps its crop inside the image', () => {
  const region = { x: 674, y: 188, width: 46, height: 20 };

  assert.equal(app.sourceImageViewBox(816, 720, region), '408 96 408 204');
});

test('the source image and highlight use the same crop', () => {
  const region = { x: 674, y: 188, width: 46, height: 20 };

  assert.deepEqual({ ...app.sourceImagePlacement(816, 720, region) }, {
    viewBox: '408 96 408 204',
    width: 200,
    left: -100,
    top: -47.05882352941176
  });
});

test('opening an image without a region clears the previous modal highlight', () => {
  const rectangle = { attributes: {}, setAttribute(name, value) { this.attributes[name] = value; } };
  const highlight = {
    attributes: { hidden: '' },
    setAttribute(name, value) { this.attributes[name] = value; },
    removeAttribute(name) { delete this.attributes[name]; },
    querySelector() { return rectangle; }
  };

  app.setSourceImageHighlight(highlight, { x: 650, y: 341, width: 71, height: 22 }, 816, 720);
  assert.equal('hidden' in highlight.attributes, false);

  app.setSourceImageHighlight(highlight, null, 816, 720);
  assert.equal('hidden' in highlight.attributes, true);
});

test('hidden source highlights are not painted', () => {
  assert.match(css, /\.source-image-highlight\[hidden\]\s*{\s*display: none;/);
});

test('the source preview crop is exactly two by one inside the bordered button', () => {
  // The image's top is a percentage of the crop's height, so a border inside the ratio box would skew it.
  assert.doesNotMatch(css, /\.source-image\.has-source-region\s*{[^}]*aspect-ratio/);
  assert.match(css, /\.source-image-crop\s*{[^}]*aspect-ratio: 2 \/ 1;/);
  app.set({ payload: { version: 1 } });
  const html = app.renderSource({ image: 'source.png', table: 'Income', region: { x: 0, y: 0, width: 10, height: 10 } });
  assert.match(html, /<span class="source-image-crop">/);
});
