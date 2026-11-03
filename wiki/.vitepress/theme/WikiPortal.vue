<script setup lang="ts">
import { computed } from 'vue';
import { useData, withBase } from 'vitepress';
import manifest from '../../content-manifest.json';
const { lang } = useData();
const locale = computed(() => lang.value.startsWith('ru') ? 'ru' : 'en');
const ru = computed(() => locale.value === 'ru');
const href = (id: string) => withBase('/'+locale.value+'/'+(id==='index'?'':id));
const routes = computed(() => ru.value ? [
 ['01','Начать играть','Установить мод, войти на сервер и сохранить первую колонию.','players/getting-started'],
 ['02','Поднять сервер','Хостинг, правила мира, доступ, резервные копии и восстановление.','admins/getting-started'],
 ['03','Участвовать в разработке','Архитектура, сборка, протокол и проверка изменений.','developers/architecture']
] : [
 ['01','Start playing','Install the mod, join a server and save your first colony.','players/getting-started'],
 ['02','Host a server','Hosting, world policy, access, backups and recovery.','admins/getting-started'],
 ['03','Contribute','Architecture, builds, protocol and verification.','developers/architecture']
]);
const steps = computed(() => ru.value ? [['Установка','players/install'],['Подключение','players/connect'],['Первый мир','players/world'],['Сохранение','players/saves-recovery']] : [['Install','players/install'],['Connect','players/connect'],['First world','players/world'],['Save safely','players/saves-recovery']]);
</script>
<template>
 <div class="wiki-portal">
  <header class="portal-intro">
   <div><p class="portal-kicker">RIMLINK / {{ ru ? 'ПОЛЕВОЕ РУКОВОДСТВО' : 'FIELD GUIDE' }}</p>
    <h1>{{ ru ? 'Своя колония. Общий мир.' : 'Your colony. A shared world.' }}</h1>
    <p>{{ ru ? 'Постоянный мультиплеер для RimWorld 1.7. От первого входа до совместной игры и управления сервером — действия, правила и восстановление без догадок.' : 'Persistent multiplayer for RimWorld 1.7. From your first login to cooperative play and server operations — practical steps, rules and recovery.' }}</p>
    <p class="portal-version">Rimlink {{ manifest.productVersion }} · {{ ru ? 'Протокол' : 'Protocol' }} {{ manifest.protocolVersion }} · 45 {{ ru ? 'тем на двух языках' : 'topics in two languages' }}</p>
   </div>
   <img class="wiki-art portal-art" :src="withBase('/illustrations/world.svg')" :alt="ru ? 'Независимые колонии соединены общим серверным миром.' : 'Independent colonies linked through a shared server world.'" width="960" height="440" />
  </header>
  <nav class="portal-routes" :aria-label="ru ? 'Выбери маршрут' : 'Choose a route'">
   <a v-for="r in routes" :key="r[3]" class="portal-route" :href="href(r[3])"><span class="route-number">{{ r[0] }}</span><strong>{{ r[1] }} <span aria-hidden="true">→</span></strong><span>{{ r[2] }}</span></a>
  </nav>
  <nav class="portal-steps" :aria-label="ru ? 'Первый вход: по шагам' : 'First login: step by step'"><b>{{ ru ? 'Первый вечер' : 'Your first evening' }}</b><a v-for="(s,i) in steps" :key="s[1]" :href="href(s[1])"><span>{{ i+1 }}</span>{{ s[0] }}</a></nav>
  <section class="portal-catalog" aria-labelledby="catalog-title"><h2 id="catalog-title">{{ ru ? 'Энциклопедия Rimlink' : 'The Rimlink encyclopedia' }}</h2><p>{{ ru ? 'Выбери задачу. Все статьи доступны здесь; поиск — в верхней панели (Ctrl K).' : 'Choose a task. Every article is linked here; search is in the top bar (Ctrl K).' }}</p>
   <div class="catalog-grid"><section v-for="group in manifest.groups" :key="group.id" class="catalog-group"><h3>{{ group.titles[locale] }}</h3><ul><li v-for="page in manifest.pages.filter(p=>p.group===group.id)" :key="page.id"><a class="catalog-link" :href="href(page.id)">{{ page.titles[locale] }}</a></li></ul></section></div>
  </section>
  <aside class="portal-help"><strong>{{ ru ? 'Что-то пошло не так?' : 'Something went wrong?' }}</strong><p>{{ ru ? 'Не удаляй журналы и не повторяй перевод как новую операцию.' : 'Keep recovery journals and do not recreate an unanswered transfer.' }} <a :href="href('players/troubleshooting')">{{ ru ? 'Диагностика игрока' : 'Player diagnostics' }}</a> · <a :href="href('admins/troubleshooting')">{{ ru ? 'Проверки сервера' : 'Server checks' }}</a> · <a :href="href('admins/backup-recovery')">{{ ru ? 'Восстановление' : 'Recovery' }}</a></p></aside>
 </div>
</template>
