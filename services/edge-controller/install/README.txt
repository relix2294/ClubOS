ClubOS Edge для сервера клуба (Windows 10/11 или Windows Server, x64). .NET устанавливать не нужно.

1. Распакуйте архив в любую папку (например C:\ClubOS-Edge-setup).
2. Дважды щёлкните INSTALL.cmd и согласитесь на запуск от администратора.
3. Ответьте на вопросы:
   - адрес облака ClubOS (как в браузере, например https://clubos.example.tj)
   - одноразовый токен Edge: Admin Web -> «Подключение» -> «Токен для Edge Controller»
4. На дашборде Admin Web появится «Edge на связи». Мастер покажет адрес для игровых ПК.

Обновление: распакуйте новую версию и снова запустите INSTALL.cmd (токен не нужен).
Удаление: PowerShell от администратора -> .\uninstall-edge.ps1
Подробно: docs/runbooks/edge-windows-install.md в репозитории ClubOS.
