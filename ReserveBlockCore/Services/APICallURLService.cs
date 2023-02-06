﻿using Newtonsoft.Json;
using ReserveBlockCore.Models;
using ReserveBlockCore.Utilities;
using System.Text;

namespace ReserveBlockCore.Services
{
    public class APICallURLService
    {
        public static async void CallURL(Transaction transaction)
        {
            try
            {
                var url = Globals.APICallURL;                
                string json = JsonConvert.SerializeObject(transaction);
                var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
                using (var client = Globals.HttpClientFactory.CreateClient())
                {
                    var httpResponse = await client.PostAsync(url, httpContent);
                    if (Globals.APICallURLLogging == true)
                    {
                        //Will only accept a string response. 
                        var httpResult = await httpResponse.Content.ReadAsStringAsync();
                        LogUtility.Log($"Transaction was sent. Here is response: {httpResult}", "BlockValidatorService.ValidateBlock()");
                    }
                }
            }
            catch (Exception ex)
            {
                if (Globals.APICallURLLogging == true)
                {
                    ErrorLogUtility.LogError($"Error Sending Transaction to URL. Error Message: {ex.ToString()}", "BlockValidatorService.ValidateBlock()");
                }
            }
        }
    }
}
