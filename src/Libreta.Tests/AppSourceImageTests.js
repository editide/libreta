const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.js'), 'utf8');
const css = fs.readFileSync(path.join(__dirname, '..', 'Libreta', 'wwwroot', 'app.css'), 'utf8');
const viewBoxSource = app.match(/function sourceImageViewBox\([\s\S]*?\n\}/)?.[0];
assert.ok(viewBoxSource, 'app.js must define sourceImageViewBox');
const sourceImageViewBox = vm.runInNewContext(`(${viewBoxSource})`);
const placementSource = app.match(/function sourceImagePlacement\([\s\S]*?\n\}/)?.[0];
assert.ok(placementSource, 'app.js must define sourceImagePlacement');
const sourceImagePlacement = vm.runInNewContext(`(${placementSource})`, { sourceImageViewBox });
const highlightSource = app.match(/function setSourceImageHighlight\([\s\S]*?\n\}/)?.[0];
assert.ok(highlightSource, 'app.js must define setSourceImageHighlight');
const setSourceImageHighlight = vm.runInNewContext(`(${highlightSource})`);

test('the source preview crops around the selected source region', () => {
  const region = { x: 650, y: 341, width: 71, height: 22 };

  assert.equal(sourceImageViewBox(816, 720, region), '390 245.5 426 213');
});

test('the source preview keeps its crop inside the image', () => {
  const region = { x: 674, y: 188, width: 46, height: 20 };

  assert.equal(sourceImageViewBox(816, 720, region), '408 96 408 204');
});

test('the source image and highlight use the same crop', () => {
  const region = { x: 674, y: 188, width: 46, height: 20 };

  assert.deepEqual({ ...sourceImagePlacement(816, 720, region) }, {
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

  setSourceImageHighlight(highlight, { x: 650, y: 341, width: 71, height: 22 }, 816, 720);
  assert.equal('hidden' in highlight.attributes, false);

  setSourceImageHighlight(highlight, null, 816, 720);
  assert.equal('hidden' in highlight.attributes, true);
});

test('hidden source highlights are not painted', () => {
  assert.match(css, /\.source-image-highlight\[hidden\]\s*{\s*display: none;/);
});

test('the source preview crop is exactly two by one inside the bordered button', () => {
  // The image's top is a percentage of the crop's height, so a border inside the ratio box would skew it.
  assert.doesNotMatch(css, /\.source-image\.has-source-region\s*{[^}]*aspect-ratio/);
  assert.match(css, /\.source-image-crop\s*{[^}]*aspect-ratio: 2 \/ 1;/);
  assert.match(app, /<span class="source-image-crop">/);
});
