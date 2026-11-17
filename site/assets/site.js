(() => {
  'use strict';

  // 演示只切换本地示意，不请求翻译接口，避免访客把营销演示当成真实翻译结果。
  const tabs = [...document.querySelectorAll('[data-demo]')];
  const instructions = {
    sentence: '选中文字 → 点「翻译」→ 查看译文',
    word: '单个英文单词 → 本地词典 → 保存与复习',
    image: '框选屏幕 → 识别文字 → 对照译图'
  };
  const selectDemo = (selected) => {
    tabs.forEach((tab) => {
      const active = tab === selected;
      tab.setAttribute('aria-selected', String(active));
      tab.tabIndex = active ? 0 : -1;
      document.getElementById(tab.getAttribute('aria-controls')).hidden = !active;
    });
    document.getElementById('demo-instruction').textContent = instructions[selected.dataset.demo];
    document.querySelector('.demo-reset').hidden = selected.dataset.demo !== 'sentence';
  };
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => selectDemo(tab));
    tab.addEventListener('keydown', (event) => {
      let next;
      if (event.key === 'ArrowRight') next = (index + 1) % tabs.length;
      if (event.key === 'ArrowLeft') next = (index + tabs.length - 1) % tabs.length;
      if (event.key === 'Home') next = 0;
      if (event.key === 'End') next = tabs.length - 1;
      if (next === undefined) return;
      event.preventDefault();
      selectDemo(tabs[next]);
      tabs[next].focus();
    });
  });

  const result = document.getElementById('sentence-result');
  let resultAnimation;
  document.querySelector('.demo-reset').addEventListener('click', () => {
    result.classList.add('is-waiting');
    document.getElementById('demo-instruction').textContent = '已选中文字，点击蓝色「翻译」按钮';
    document.querySelector('.toolbar-translate').focus({ preventScroll: true });
  });
  document.querySelector('.toolbar-translate').addEventListener('click', () => {
    // 静态首屏保留最终效果；主动重选才回到待翻译态，避免加载动画让人误以为选词会自动请求。
    result.classList.remove('is-waiting');
    resultAnimation?.cancel();
    if (!matchMedia('(prefers-reduced-motion: reduce)').matches) {
      resultAnimation = result.animate(
        [{ opacity: 0.3, transform: 'translateY(7px)' }, { opacity: 1, transform: 'none' }],
        { duration: 260, easing: 'ease-out' }
      );
    }
    document.getElementById('demo-instruction').textContent = '译文已在原文旁显示 · 此处为交互示意';
  });

  const screenshot = document.getElementById('app-screenshot');
  const themeButtons = [...document.querySelectorAll('[data-theme]')];
  themeButtons.forEach((button) => button.addEventListener('click', () => {
    const dark = button.dataset.theme === 'dark';
    screenshot.src = `assets/img/main-${dark ? 'dark' : 'light'}.png`;
    screenshot.alt = `Fast Translation ${dark ? '深色' : '浅色'}主窗口，左侧为功能导航，右侧为原文和译文区域`;
    screenshot.closest('figure').classList.toggle('is-dark', dark);
    document.getElementById('theme-caption').textContent = `${dark ? '深色' : '浅色'}主题 · 也可在软件中跟随系统切换`;
    themeButtons.forEach((item) => item.setAttribute('aria-pressed', String(item === button)));
  }));

  // 最后再显示增强控件，脚本缺失或未执行时，静态示例与下载入口仍可完整阅读。
  document.documentElement.classList.add('js');
})();
