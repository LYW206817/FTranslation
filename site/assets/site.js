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

  const setupScrollReveal = () => {
    if (!('IntersectionObserver' in window)) return;
    const motion = matchMedia('(prefers-reduced-motion: reduce)');
    const targets = [...document.querySelectorAll(
      '.contexts, .section-heading > *, .feature-card, .speech-feature, .learning-copy, ' +
      '.learning-visual, .interface-toolbar, .app-preview, .interface-notes, .center-heading, ' +
      '.plan, .plans-note, .faq-section > div:first-child, .faq-list details, .download-intro, .download-option'
    )];
    // v163：出现与复位分用两个区域，卡片的入场位移不会在视口边缘触发隐藏/出现循环。
    const entrance = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (entry.isIntersecting && entry.intersectionRatio >= 0.16) entry.target.classList.add('is-visible');
      });
    }, { threshold: 0.16, rootMargin: '0px 0px -32px 0px' });
    const departure = new IntersectionObserver((entries) => {
      entries.forEach((entry) => {
        if (!entry.isIntersecting) entry.target.classList.remove('is-visible');
      });
    }, { rootMargin: '96px 0px' });
    targets.forEach((target) => {
      target.classList.add('scroll-reveal');
      const siblings = [...target.parentElement.children];
      const staggered = target.parentElement.matches('.plans, .feature-grid, .learning-grid, .section-heading, .faq-list');
      target.style.setProperty('--reveal-delay', `${staggered ? Math.min(siblings.indexOf(target), 3) * 80 : 0}ms`);
      target.style.setProperty('--reveal-distance', `${Math.min(24, target.offsetHeight * 0.1)}px`);
    });
    const syncMotion = () => {
      entrance.disconnect();
      departure.disconnect();
      targets.forEach((target) => {
        target.classList.toggle('is-visible', motion.matches);
        if (!motion.matches) {
          entrance.observe(target);
          departure.observe(target);
        }
      });
    };
    motion.addEventListener('change', syncMotion);
    syncMotion();
  };
  setupScrollReveal();
})();
