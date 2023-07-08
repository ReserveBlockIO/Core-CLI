﻿namespace ReserveBlockCore.Models.SmartContracts
{
    public class TokenDetails
    {
        public string TokenName { get; set; }
        public string TokenTicker { get; set; }
        public decimal StartingSupply { get; set; }
        public decimal CurrentSupply { get; set; }
        public int DecimalPlaces { get; set; }
        public bool IsPaused { get; set; }
        public string ContractOwner { get; set; }
        public List<string>? AddressBlackList { get; set; }

        public static TokenDetails CreateTokenDetails(TokenFeature tokenFeature, SmartContractMain scMain)
        {
            TokenDetails tokenDetails = new TokenDetails {
                TokenName = tokenFeature.TokenName,
                TokenTicker = tokenFeature.TokenTicker,
                StartingSupply = tokenFeature.TokenSupply,
                CurrentSupply = tokenFeature.TokenSupply,
                IsPaused = false,
                ContractOwner = scMain.MinterAddress,
                DecimalPlaces = tokenFeature.TokenDecimalPlaces
            };

            return tokenDetails;
        }
    }
}
