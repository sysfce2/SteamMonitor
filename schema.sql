CREATE TABLE `CMs` (
  `Address` varchar(50) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `IsWebSocket` tinyint(1) NOT NULL DEFAULT 0,
  `Status` smallint(3) NOT NULL,
  `LastUpdate` datetime NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp()
) ENGINE=MEMORY DEFAULT CHARSET=ascii COLLATE=ascii_bin;

ALTER TABLE `CMs`
  ADD PRIMARY KEY (`Address`,`IsWebSocket`),
  ADD KEY `Status` (`Status`);
