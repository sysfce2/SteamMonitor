CREATE TABLE `CMs` (
  `Address` varchar(50) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `Datacenter` varchar(5) CHARACTER SET ascii COLLATE ascii_bin NOT NULL,
  `IsWebSocket` tinyint(1) NOT NULL DEFAULT 0,
  `Status` smallint(6) NOT NULL,
  `LastUpdate` datetime NOT NULL DEFAULT current_timestamp() ON UPDATE current_timestamp(),
  PRIMARY KEY (`Address`,`IsWebSocket`),
  KEY `Status` (`Status`)
) ENGINE=InnoDB DEFAULT CHARSET=ascii COLLATE=ascii_bin;
