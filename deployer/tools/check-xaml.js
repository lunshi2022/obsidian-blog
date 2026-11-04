const fs = require('fs');

// 检测 WPF XAML 的致命结构错误：元素的"属性元素"出现在"内容子元素"之后
// 例如 <Grid><Rectangle/><Grid.ColumnDefinitions>…</Grid.ColumnDefinitions></Grid>
// 这类错误 csc 编译能过，运行时报 XamlDuplicateMemberException("已对 Grid 设置 Children 属性")

const file = process.argv[2];
let text = fs.readFileSync(file, 'utf8');

// 注释替换成等量换行，保证行号不偏移
text = text.replace(/<!--[\s\S]*?-->/g, (m) => m.replace(/[^\n]/g, ' '));

const tagRe = /<(\/?)([A-Za-z_][\w.:]*)((?:[^>"']|"[^"]*"|'[^']*')*?)(\/?)>/g;

const stack = [];
const problems = [];
const lineStarts = [0];
for (let i = 0; i < text.length; i++) if (text[i] === '\n') lineStarts.push(i + 1);
function lineOf(idx) {
  let lo = 0, hi = lineStarts.length - 1;
  while (lo < hi) {
    const mid = (lo + hi + 1) >> 1;
    if (lineStarts[mid] <= idx) lo = mid; else hi = mid - 1;
  }
  return lo + 1;
}

let m;
while ((m = tagRe.exec(text)) !== null) {
  const closing = m[1] === '/';
  const tag = m[2];
  const selfClose = m[4] === '/';

  if (closing) {
    if (stack.length) stack.pop();
    continue;
  }

  const parent = stack.length ? stack[stack.length - 1] : null;
  const isProp = tag.indexOf('.') > 0;
  const propName = isProp ? tag.slice(tag.indexOf('.') + 1) : '';

  if (parent) {
    if (isProp) {
      // ControlTemplate / DataTemplate 的 Triggers 按惯例写在内容之后，是合法的
      const templateTriggers = propName === 'Triggers' && /Template$/.test(parent.tag);
      if (parent.hasContent && !templateTriggers) {
        problems.push({
          line: lineOf(m.index),
          tag: tag,
          parent: parent.tag,
          parentLine: parent.line,
        });
      }
      parent.hasProp = true;
    } else {
      parent.hasContent = true;
    }
  }

  if (!selfClose) {
    stack.push({ tag: tag, line: lineOf(m.index), hasContent: false, hasProp: false });
  }
}

if (problems.length === 0) {
  console.log('✓ 结构检查通过：没有"属性元素写在内容之后"的问题');
} else {
  console.log('✗ 发现 ' + problems.length + ' 处结构错误：');
  for (const p of problems) {
    console.log('  第 ' + p.line + ' 行 <' + p.tag + '> 写在内容之后（父元素 <' + p.parent + '> 起于第 ' + p.parentLine + ' 行）');
  }
  console.log('  修法：把属性元素（*.Xxx）移到该元素的第一个内容子元素之前。');
}
process.exit(problems.length === 0 ? 0 : 1);
